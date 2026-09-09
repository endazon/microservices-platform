# 合成監視（synthetic monitoring）— opt-in オーバーレイ

低頻度経路（`/analysis/ask` 系）へ一定間隔で代表リクエストを打ち、SLO の**評価対象そのもの**を
存在させる常駐プローブである。**クラスタ内で完結し、外部の監視 SaaS は使わない。**

🔴 **標識と除外が揃っていない構成では配備しない。** 合成トラフィックが利用実績・費用・検索傾向へ
混ざると、それらの指標が「人が使った量」を表さなくなる。除外は BFF・DashboardService・LlmGateway に
入っており、**この overlay を当てる前にそれらのイメージが更新されていること**を確かめること。

## 起動器から当てる（`SYNTHETIC=1`・#1287）

🔵 **［2026-09-09 追加 / #1287］下の「前提と手順」を 1 コマンドにまとめた口が起動器に在る。**

```console
SYNTHETIC=1 bash scripts/k8s-local-up.sh
```

門は手順 1〜4 と**同じことを同じ順で**行う（realm 追随 → 標識の env → 除外 3 サービスの rollout →
overlay の apply → プローブの rollout 待ち）。差分は 2 点だけである。

- **Secret は dev の置き値で作る**（`synthetic-monitor-dev-secret-change-me`。他の dev クライアントと同じ扱い）。
  `SYNTHETIC_MONITOR_CLIENT_SECRET=... SYNTHETIC=1 bash scripts/k8s-local-up.sh` で上書きできる。
  **`ESO=1` を併用すると Vault → ExternalSecret 供給へ委譲する**（手動 apply はしない。二重所有回避）。
- 🔴 **除外の 3 サービスが揃わなければ、プローブを配備せずに `up` が落ちる**
  （ADR-0076 決定 4「除外できない構成では配備しない」。警告して続行はしない）。

🔴 **既定はオフのままである。** ADR-0079 §フォローアップ 1 の「既定の起動器へ入れる」は**本番構成**
（`deploy/helm/microservices-platform`）を指しており、そちらには合成監視が無い。加えてローカルで既定 ON に
すると、捨てるつもりの dev クラスタが常に `/analysis/ask` 系を叩き続ける。**既定 ON を望むなら
`scripts/k8s-local-up.sh` の `SYNTHETIC_DEFAULT` を `"1"` にするだけでよい**（他は 1 行も変えなくてよい）。

🔴 **ローカルでは「除外規則が入ったイメージであること」が構造的に満たされる** ——
`scripts/k8s-local-images.sh` が**この作業ツリーのソースから**イメージを作るためである。
**稼働クラスタ（レジストリのタグを引く）へこの根拠は移せない。**

⚠️ **`ARGOCD=1` で同期させているクラスタでは、標識の env が ArgoCD に巻き戻される**
（門は live の Deployment を触るが、chart には無い設定であるため）。その構成では `up` を打ち直すか、
標識を chart 側へ入れる別の手当てが要る。

## 前提と手順（手で当てる場合）

1. **realm クライアントを反映する。** `deploy/keycloak/microservices-platform-realm.json` に
   `synthetic-monitor`（`client_credentials`・ロール無し・ABAC ポリシー無し）を宣言済み。
   稼働 realm へは realm reconcile Job で当てる（`deploy/local/keycloak-setup/reconcile-realm.sh`）。

2. **Secret を作る（リポジトリには置かない）。** Keycloak の管理画面か Admin REST API で
   `synthetic-monitor` のシークレットを再生成し、その値で作る。

   ```console
   kubectl -n microservices-platform create secret generic synthetic-monitor-oidc \
     --from-literal=client-secret='<Keycloak が発行した値>'
   ```

   realm JSON の `synthetic-monitor-dev-secret-change-me` は**開発用の置き値**であり、
   **本番ではそのまま使わない**（`abac-seeder` / `ai-stock-trading-kb-writer` と同じ扱い）。
   🔵 ［2026-09-09 補足 / #1287］**ローカルの dev クラスタでは置き値をそのまま使う** ——
   起動器の `SYNTHETIC=1` は他の dev OIDC クライアント（`bff-oidc` / `headlamp-oidc` …）と同じく
   この値で Secret を作る。**実 realm の値を再生成した環境ではズレるので、上の手順で作り直すこと。**

3. **BFF・DashboardService・AiAnalysisService へ標識の許可集合を渡す。** 空だと
   **何も合成と見なさない**（fail-closed）ため、除外は 1 件も効かない。

   ```
   SyntheticMonitoring__Subjects__0 = synthetic-monitor
   ```

   `SyntheticMonitoring__AllowLlmEgress` は**このオーバーレイでは設定しない**（既定 false）。
   🔵 ［2026-09-05 更新 / #1203］従前の理由は「上限の定めがないまま費用が発生するから（裁定待ち）」だった。
   **裁定は下りた**（ADR-0079 決定 1・2）—— LLM を呼ぶ合成は**別の配備単位**（間隔 60 分）として置く
   と定められており、**本オーバーレイ（60 秒・常時トラフィック用）では呼ばないことが確定値**である。

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

## 頻度と費用の上限（ADR-0079 で確定した）

🔵 **［2026-09-05 更新 / #1203］本節は従前「暫定値」「裁定待ち」と書いていた。裁定が下りた。**
`ADR-0079` 決定 1・2（利用者裁定 2026-09-05・環流 planning#538）が確定させた値は次の 2 段である。

| 用途 | 間隔 | LLM を呼ぶか | 本オーバーレイ |
| --- | --- | --- | --- |
| **常時トラフィックの生成** | **60 秒** | **呼ばない**（検索までは走る） | ✅ **これである** |
| **SLO 評価用** | **60 分** | **呼ぶ** | ❌ **別の配備単位。未着手**（同 §フォローアップ 2） |

- **頻度は配備時に与える。** `PROBE_INTERVAL_SECONDS` に既定値は無く、未設定ならプローブは起動しない
  （**実装が数字を決めない**という作法は裁定後も変えない）。マニフェストの `60` は**確定値**である。
- **費用の上限は絶対額で置かない。間隔が実質的に固定する** —— 60 分側は月 720 回で、概算 月約 4,400 円。
  **本オーバーレイは LLM を呼ばないので、これを当てても費用は 0 である。**
- 🔴 **その代わり、初回トークン（`NFR-02` の SLI）の評価対象は生まれない** ——
  計器は `token` イベントが出て初めて記録されるためである。
  **ここが空いているのは裁定待ちだからではなく、60 分側の配備が未着手だからである。**

## `absent` の併設との関係（#1203）

**本オーバーレイを当てて初めて、`/analysis/ask` の HTTP 系列が常時存在する。**
`deploy/prometheus/alerts.yml` の `RagLatencySeriesAbsent` はそれを前提に置いてある。

🔴 **当てていないクラスタでは `RagLatencySeriesAbsent` は真になる。** これは誤報ではなく
**「SLO の評価対象が本当に無い」状態**である（クラスタ再作成中に鳴るのと同じ扱い）。
**既定の起動器へ入れる条件は「除外を含むイメージが配備されていること」である**（`ADR-0079` §フォローアップ 1）。
🔵 ［2026-09-09 追加 / #1287］**ローカル起動器には `SYNTHETIC=1` の口を用意した**（上節）。
**本番構成（helm）への投入・稼働クラスタでの実測は利用者の手が要るため未着手である。**
