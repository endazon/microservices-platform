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
（`deploy/helm/microservices-platform`）を指している。🔵 ［2026-09-26 更新 / #1287］従前ここには「そちらには
合成監視が無い」と書いてあった。**チャートに既定オフの口（`syntheticMonitor.enabled`）を用意した**（下節
「本番構成（helm チャート）で有効にする」）。加えてローカルで既定 ON に
すると、捨てるつもりの dev クラスタが常に `/analysis/ask` 系を叩き続ける。**既定 ON を望むなら
`scripts/k8s-local-up.sh` の `SYNTHETIC_DEFAULT` を `"1"` にするだけでよい**（他は 1 行も変えなくてよい）。

🔴 **ローカルでは「除外規則が入ったイメージであること」が構造的に満たされる** ——
`scripts/k8s-local-images.sh` が**この作業ツリーのソースから**イメージを作るためである。
**稼働クラスタ（レジストリのタグを引く）へこの根拠は移せない。**

⚠️ **`ARGOCD=1` で同期させているクラスタでは、標識の env が ArgoCD に巻き戻される**
（門は live の Deployment を触るが、chart の既定の姿には無い設定であるため）。その構成では `up` を打ち直すか、
🔵 ［2026-09-26 更新 / #1287］**門ではなくチャート側で有効にする**（下節。`SYNTHETIC=1` とは併用しない）。

## 本番構成（helm チャート）で有効にする（#1287）

🔵 **［2026-09-26 追加 / #1287・利用者裁定 案 A］** チャート `deploy/helm/microservices-platform` に
`syntheticMonitor.enabled`（**既定 `false`**）を用意した。**既定のままでは 1 バイトも描画されない** ——
プローブも、3 サービスの標識の env も出ない（従前の描画とバイト等価。何も呼ばず、何も費やさない）。

有効にすると、**同じ描画で**次が揃う（「標識と除外は同時に入れる」）。

| 何 | 中身 |
| --- | --- |
| プローブ | `Deployment/synthetic-monitor` ＋ `ConfigMap/synthetic-monitor-probe`（**本 overlay と同名・同じ env・同じイメージ**。`probe.js` はチャートの `files/synthetic-monitor/probe.js` に写しを置き、バイト一致を試験が固定する） |
| 標識 | `bff` / `dashboard` / `aianalysis` の env `SyntheticMonitoring__Subjects__0=synthetic-monitor`（門と同じ 3 つ。集合は values の knob にしていない） |
| egress | `networkPolicy.enabled` のとき、プローブ → Keycloak（`platform-infra` の `app: keycloak`・8080）の 1 本 |
| Secret | **チャートは参照だけ**（`existingSecret: synthetic-monitor-oidc`）。作るのは Vault → `deploy/local/vault/eso/externalsecret-synthetic-monitor-oidc.yaml` → Secret（チャートの外。他の OIDC クライアントと同じ流儀） |

🔴 **描画の段階で止める構成**（`helm template` / `helm upgrade` が失敗する）:
3 サービスのどれかが `enabled: false`（除外できない構成では配備しない）／`aianalysis` の `extraEnv` / `extraEnvAppend` に
`SyntheticMonitoring__AllowLlmEgress` が `false` 以外で立っている（60 秒のプローブが LLM を呼ぶと月 43,200 回）。
鍵の綴りの揺れ（大文字小文字・`:` 区切り・`DOTNET_` / `ASPNETCORE_` 接頭辞）も .NET が同じ鍵として読むので同じく止め、
値はリテラルの `false` だけを通す（Secret 参照は中身を確かめられないので止める）。
**LLM を呼ぶ 60 分側は別の配備単位であり、課金の承認が先である**（本チャートにその knob は無い）。

### 🙏 利用者の手順

1. **前提: 3 サービス（BFF / DashboardService / AiAnalysisService）と LlmGateway のイメージを develop から作り直して配備する**（#1378）。
   🔴 **チャートはイメージの中身を確かめられない。** 稼働クラスタの `latest` は #1378 の実測（2026-09-11）で**約 7 週間前**であり、
   除外規則（PR #1259 で入った標識の判定と除外）を含まない。**その状態で有効にすると、除外できない構成へ合成を流すことになる。**
2. **realm に `synthetic-monitor` クライアントを反映し、その secret を Vault `secret/msp/synthetic-monitor-oidc`（`client-secret`）へ入れる。**
   ExternalSecret を当てて `SecretSynced` を待つ（env は Pod 起動時に 1 度だけ解決される。先に Pod が立つと空の値で固定される）:

   ```console
   kubectl apply -f deploy/local/vault/eso/externalsecret-synthetic-monitor-oidc.yaml
   kubectl -n microservices-platform wait --for=condition=Ready externalsecret/synthetic-monitor-oidc --timeout=120s
   ```

3. **有効にする**（ArgoCD なら Application の values / `helm.parameters`、手で当てるなら `--set`）:

   ```console
   helm upgrade <release> deploy/helm/microservices-platform -n microservices-platform --reuse-values \
     --set syntheticMonitor.enabled=true
   ```

   🔴 **ローカルの `SYNTHETIC=1`（本 overlay）と併用しない**（同名の資源になり、後から当てた側が上書きする）。
4. **確かめる** —— 上の「効いていることの確かめ方」がそのまま使える（資源名が同じ）。issue #1287 の受け入れ基準 ①②
   （`RagLatencySeriesAbsent` が鳴らないこと／プローブを止めると鳴ること）はここで実測する。
5. **止める** —— `--set syntheticMonitor.enabled=false` で、プローブ・標識・egress が同じ描画から一度に消える。
   一時的に止めるだけなら `kubectl -n microservices-platform scale deploy/synthetic-monitor --replicas=0`（ArgoCD の自己修復が有効なら戻される）。

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
🔵 ［2026-09-26 更新 / #1287］**本番構成（helm）には既定オフの口を用意した**（上節「本番構成（helm チャート）で有効にする」）。
**有効化（イメージの再ビルドが前提）と稼働クラスタでの実測は利用者の手が要るため未着手である。**
