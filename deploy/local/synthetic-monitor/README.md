# 合成監視（synthetic monitoring）— opt-in オーバーレイ

低頻度経路（`/analysis/ask` 系）へ一定間隔で代表リクエストを打ち、SLO の**評価対象そのもの**を
存在させる常駐プローブである。**クラスタ内で完結し、外部の監視 SaaS は使わない。**

🔴 **標識と除外が揃っていない構成では配備しない。** 合成トラフィックが利用実績・費用・検索傾向へ
混ざると、それらの指標が「人が使った量」を表さなくなる。除外は BFF・DashboardService・LlmGateway に
入っており、**この overlay を当てる前にそれらのイメージが更新されていること**を確かめること。

## 前提と手順

1. **realm クライアントを反映する。** `deploy/keycloak/microservices-platform-realm.json` に
   `synthetic-monitor`（`client_credentials`・ロール無し・ABAC ポリシー無し）を宣言済み。
   稼働 realm へは realm reconcile Job で当てる（`deploy/local/keycloak-setup/reconcile-realm.sh`）。

2. **Secret を作る（リポジトリには置かない）。** Keycloak の管理画面か Admin REST API で
   `synthetic-monitor` のシークレットを再生成し、その値で作る。

   ```console
   kubectl -n microservices-platform create secret generic synthetic-monitor-oidc \
     --from-literal=client-secret='<Keycloak が発行した値>'
   ```

   realm JSON の `synthetic-monitor-dev-secret-change-me` は**開発用の置き値**であり、そのまま使わない
   （`abac-seeder` / `ai-stock-trading-kb-writer` と同じ扱い）。

3. **BFF・DashboardService・AiAnalysisService へ標識の許可集合を渡す。** 空だと
   **何も合成と見なさない**（fail-closed）ため、除外は 1 件も効かない。

   ```
   SyntheticMonitoring__Subjects__0 = synthetic-monitor
   ```

   `SyntheticMonitoring__AllowLlmEgress` は**設定しない**（既定 false）。true にすると合成が
   実際に LLM を呼び、**上限の定めがないまま費用が発生する**（計画側の裁定待ち）。

4. **当てる。**

   ```console
   kubectl apply -k deploy/local/synthetic-monitor
   ```

## 効いていることの確かめ方

```console
# プローブが回っているか（経路・状態・所要時間だけを出す。応答内容は出さない）
kubectl -n microservices-platform logs deploy/synthetic-monitor --tail=20

# 評価対象が生まれたか（Prometheus）
#   http_server_request_duration_seconds_count{job="microservices-platform.aianalysis-service", http_route="/analysis/ask"}

# 除外が効いているか（合成のぶんだけが伸びる系列）
#   usage_event_dispatch_total{usage_event_outcome="excluded_synthetic"}
#   llm_usage_synthetic_excluded_total
```

🔴 **`excluded_synthetic` が伸び、`sent` が伸びていないときは「合成だけが通っていて実利用は 0」である。**
除外は**指標を守るためのもので、費用そのものは減らさない**。

## 停止

```console
kubectl -n microservices-platform scale deploy/synthetic-monitor --replicas=0
```

`replicas=0` で即座に止まる（次の間隔を待たない）。恒久的に外すなら
`kubectl delete -k deploy/local/synthetic-monitor` を使う。**Secret は別に消すこと。**

## 頻度と費用の上限

- **頻度は配備時に与える。** `PROBE_INTERVAL_SECONDS` に既定値は無く、未設定ならプローブは起動しない。
  マニフェストの `60` は「検知要件 5 分より十分に短い」という運用上の暫定値である。
- **費用は既定で 0 である。** 合成の要求は AiAnalysisService が LLM を呼ぶ手前で縮退させる。
  したがって現状の配備で恒常的に発生する費用は無い。
- 🔴 **その代わり、初回トークン（`NFR-02` の SLI）の評価対象は生まれない** ——
  計器は `token` イベントが出て初めて記録されるためである。**ここは裁定待ちで空いている。**
