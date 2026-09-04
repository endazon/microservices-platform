# ローカル k8s(k3d) dev 環境（MSP + AST 連結）

> 起点: [IADR-0066](../../.ai-context/adr/IADR-0066_local-k8s-dev-environment.md) /
> 作業仕様書 [`.ai-context/specs/20260713_issue-266_local-k8s-dev-env.md`](../../.ai-context/specs/20260713_issue-266_local-k8s-dev-env.md) /
> Issue #266（MSP）・AST#122（AST chart）・AST#121（K8s CronJob）

本ディレクトリは **dev 専用**の資産である。本番像（[`deploy/helm`](../helm) 本体・
[`deploy/docker-compose.yml`](../docker-compose.yml)・[`deploy/argocd`](../argocd)）は変更しない。

## 何を作るか

```
[k3d cluster: msp-ast-dev]
  ns platform-infra          postgres / rabbitmq / redis / keycloak / qdrant / otel-collector
                             + mailpit（開発環境の捕捉用 MTA。メールはここで止まり外へ出ない）  ← deploy/local/infra
  ns microservices-platform  既存 Helm chart（values-local: mesh/NP/HPA off, registry=local）
                             + ExternalName エイリアス（素のサービス名 → platform-infra）
  ns ai-stock-trading        AST chart（AST#122 で追加）
```

MSP chart はインフラを**デプロイせず**素のサービス名（`postgres` 等）で参照するため、infra を
`platform-infra` に置き、各アプリ namespace に ExternalName エイリアスを張って名前解決させる。

## 必要ツール（Windows）

k8s ランタイムは **2 択**。スクリプトは `nerdctl`/`k3d` の有無で自動判定する（`K8S_LOCAL_RUNTIME=rancher|k3d` で明示指定可）。

**A. Rancher Desktop（推奨・内蔵 k3s）** — Docker Desktop も k3d も不要。
- Preferences → **Container Engine = containerd**、**Kubernetes = 有効**。
- 同梱の `kubectl` / `nerdctl` / `helm` を使う（`nerdctl --namespace k8s.io build` で k3s に直接供給）。
- kubectl context を `rancher-desktop` にしておく。

**B. Docker Desktop + k3d**
```powershell
winget install Docker.DockerDesktop   # WSL2 backend・メモリ 8GB+ 推奨
winget install Kubernetes.kubectl
winget install k3d                    # 無ければ choco install k3d
winget install Helm.Helm
```

> どちらも実体は k3s（ADR-0008）。A は内蔵 k3s をそのまま、B は k3d が k3s-in-docker を作る。

## 起動（Git Bash 推奨。1 コマンド）

```bash
bash scripts/k8s-local-up.sh          # クラスタ作成→build/import→secret→infra→MSP chart→alias
kubectl get pods -A
kubectl -n microservices-platform port-forward svc/bff-service 5080:8080
#   → http://localhost:5080/health
```

破棄:

```bash
bash scripts/k8s-local-down.sh
```

### opt-in オーバーレイ（可観測性 / Vault / GitOps・AST#24 / IADR-0077）

既定は**無効**（env 未設定で従来どおり）。env ゲートで追加のみ有効化する（既存ステップは不変）。

```bash
OBSERVABILITY=1 bash scripts/k8s-local-up.sh   # Prometheus/Loki/Tempo/Grafana + collector forwarding
VAULT=1         bash scripts/k8s-local-up.sh   # Vault dev + ClusterSecretStore(vault-backend)（要 ESO CRD）
ARGOCD=1        bash scripts/k8s-local-up.sh   # ArgoCD install + Application 適用（MSP/AST）
PERSIST=1       bash scripts/k8s-local-up.sh   # Keycloak(realm+runtime state)/Postgres/Qdrant を PVC 永続化（OBSERVABILITY=1 併用で可観測性 4 種も。下記「永続化」節）
LOCALEDGE=1     bash scripts/k8s-local-up.sh   # ローカルエッジ集約: platform フロント 80/443 ＋ 管理ツール 50000（下記 edge 節）
ESO=1           bash scripts/k8s-local-up.sh   # Vault＋ESO で secret 自動供給（要 VAULT=1・本番同等 k8s auth・IADR-0096・#310）
```

- [`deploy/local/observability/README.md`](observability/README.md) — 可観測性スタック（既定 debug-only を維持）
- [`deploy/local/vault/README.md`](vault/README.md) — Vault dev + External Secrets（**dev 専用・平文秘密なし**）。`ESO=1` で secret 自動供給（[eso/README](vault/eso/README.md)・IADR-0096）
- [`deploy/local/argocd/README.md`](argocd/README.md) — GitOps ブートストラップ
- [`deploy/local/edge/README.md`](edge/README.md) — **ローカルエッジ集約**（platform 80/443 ＋ 管理ツール 50000・ホスト名ベース・IADR-0091）。**k3d はポート再作成が必要**（同 README のユーザー手順）
- Hetzner 実 stand-up・本番 NFR は **Tier 3**（対象外）。

### 永続化（opt-in・PERSIST=1・Issue #324 / IADR-0082、#787 / IADR-0210）

> 起点: [IADR-0082](../../.ai-context/adr/IADR-0082_local-k8s-infra-persistence.md)（Keycloak / Postgres）＋
> [IADR-0210](../../.ai-context/adr/IADR-0210_local-k8s-observability-persistence.md)（Qdrant / 可観測性 4 種） /
> 作業仕様書 [`.ai-context/specs/20260719_issue-324_infra-persistence-k8s.md`](../../.ai-context/specs/20260719_issue-324_infra-persistence-k8s.md) ・
> [`.ai-context/specs/20260816_issue-787_k8s-observability-persistence.md`](../../.ai-context/specs/20260816_issue-787_k8s-observability-persistence.md)

既定の経路B infra は [IADR-0066](../../.ai-context/adr/IADR-0066_local-k8s-dev-environment.md) の割り切りで `emptyDir`
（Pod 再起動で再 init）である。このため **Keycloak Pod が再起動するたびに realm が再 import され、管理コンソールで
加えた runtime state（追加ユーザー・シークレット・セッション等）が失われる**。`PERSIST=1` を付けると
[`deploy/local/infra-persistence`](infra-persistence/) オーバーレイが適用され、**Keycloak / Postgres / Qdrant を
`local-path` PVC で永続化**する（Pod 再起動でも状態を保持）。
**`OBSERVABILITY=1` を併用すると**、可観測性スタックも [`deploy/local/observability-persistence`](observability-persistence/)
へ切り替わり、**Prometheus / Loki / Tempo / Grafana** が永続化される。

```bash
PERSIST=1 bash scripts/k8s-local-up.sh
# → deploy/local/infra-persistence を適用（base infra + PVC + volume パッチ）。

PERSIST=1 OBSERVABILITY=1 bash scripts/k8s-local-up.sh
# → 上記に加えて deploy/local/observability-persistence を適用（素の deploy/local/observability の "置換"）。
```

| サービス | PVC | マウント | 保持されるもの | ゲート |
| --- | --- | --- | --- | --- |
| Keycloak | `keycloak-data`（1Gi・local-path） | `/opt/keycloak/data`（`start-dev` の file H2） | realm ＋ runtime state（追加ユーザー・シークレット・セッション） | `PERSIST=1` |
| Postgres | `postgres-data`（2Gi・local-path） | `/var/lib/postgresql/data` | 全アプリ DB（MSP + AST） | `PERSIST=1` |
| Qdrant | `qdrant-storage`（2Gi・local-path） | `/qdrant/storage` | コレクションとベクトル（再 ingest なしで検索を続けられる） | `PERSIST=1` |
| Prometheus | `prometheus-data`（5Gi・local-path） | `/prometheus`（TSDB） | メトリクス（保持期間は下記 args で 7d / 4GB） | `PERSIST=1` ＋ `OBSERVABILITY=1` |
| Loki | `loki-data`（2Gi・local-path） | `/tmp/loki`（config の `path_prefix`） | ログ（index / chunks） | `PERSIST=1` ＋ `OBSERVABILITY=1` |
| Tempo | `tempo-data`（2Gi・local-path） | `/tmp/tempo`（`local.path` / `wal.path` の親） | トレース（blocks / wal） | `PERSIST=1` ＋ `OBSERVABILITY=1` |
| Grafana | `grafana-data`（1Gi・local-path） | `/var/lib/grafana` | UI から import したダッシュボード・silences・ユーザー設定 | `PERSIST=1` ＋ `OBSERVABILITY=1` |

- **Prometheus の保持期間**は base（[`observability/prometheus.yaml`](observability/prometheus.yaml)）の args
  `--storage.tsdb.retention.time=7d` / `--storage.tsdb.retention.size=4GB` で明示する。**`size` を PVC 容量（5Gi）
  未満に置いてあるので、流入が増えても PVC が満杯になって書き込み不能になることはない**（IADR-0210 決定 3）。
  compose（`deploy/docker-compose.yml`）にも同じ 2 引数がある（パリティ）。
- **Pod は root へ落とさない**（`securityContext` は 4 種とも付けない）。compose の `user: "0:0"`（IADR-0079 §3）は
  **docker の named volume が root:root 0755 で生成される**ことへの対処であって、**k8s へは転用できない** ——
  local-path provisioner は `mkdir -m 0777` でボリュームディレクトリを作る（`kube-system/local-path-config`）。
  実測（2026-08-16・稼働中の k3s）: loki（uid 10001）／tempo（uid 10001）／grafana（uid 472）が
  いずれも非 root のまま `drwxrwxrwx` の PVC へ書き、**4 件とも再起動 0 回で Ready** だった（IADR-0210 決定 6）。
- **PVC を掴む Deployment は `strategy: Recreate`** になる（postgres / keycloak / qdrant ＋ 可観測性 4 種の 7 件）。
  `ReadWriteOnce` と `RollingUpdate` は両立しない。local-path は単一ノードの hostPath なので**スケジューリングでは
  詰まらず、アプリのロックで詰まる** —— Prometheus は `storage.tsdb.no-lockfile=false`（`/api/v1/status/flags`）で、
  再起動後の `/prometheus/data` に `lock` が実在した。**base（emptyDir）側は RollingUpdate のまま**である。
- **⚠️ PVC の要求容量は縮小できない。** 上表の容量を小さくする変更を**既存クラスタへ再 apply すると API サーバが拒否する**
  （実測: `spec.resources.requests.storage: Forbidden: field can not be less than status.capacity`）。
  縮小したいときは対象 Deployment を `--replicas=0` にしてから PVC を消して作り直す（＝データは失われる）。
- **保持されるのは Pod の再起動/再作成の範囲**。`bash scripts/k8s-local-down.sh` は k3d 経路ではクラスタごと、
  Rancher Desktop 経路では `platform-infra` namespace を削除するため、**`down`→`up` の再構築サイクルでは PVC
  （上表のすべて）も消える**（= realm/DB/embeddings/メトリクスは再生成）。PVC を残したまま作り直したいときは `down` を
  使わず `kubectl -n platform-infra rollout restart deploy/keycloak deploy/postgres` 等で Pod のみ入れ替える。
- **既定（`PERSIST` 未設定）は従来どおり `emptyDir`**（挙動不変・後方互換・fail-safe）。`local-path` 等の
  provisioner が無いクラスタでも既定経路は Pod Pending 化しない。**rabbitmq / redis / otel は emptyDir 継続**
  （queue/cache は揮発前提・otel は stateless。詳細は IADR-0082。**qdrant は #787 / IADR-0210 で永続化対象へ移した**）。
- **⚠️ 既存環境の移行**: 途中から `PERSIST=1` に切り替えると Deployment の volume が差し替わりローリング更新が走る。
  **初回は空 PVC のため realm/DB は import/init で再生成**される（既存 emptyDir のデータは元々 Pod 生存期間のみの揮発
  データで、失う恒久データは無い）。以後の再起動では PVC のデータが保持される。リセットしたいときは PVC を消す:
  ```bash
  kubectl -n platform-infra delete pvc keycloak-data postgres-data qdrant-storage   # 次回 PERSIST=1 起動で空から再生成
  # 可観測性側（PERSIST=1 + OBSERVABILITY=1 で作られる分）:
  kubectl -n platform-infra delete pvc prometheus-data loki-data tempo-data grafana-data
  ```

#### ⚠️ realm（`microservices-platform-realm.json`）を更新したときの反映手順

永続化後は `--import-realm` が **既存 realm をスキップ**（既存を上書きしない）するため、`realm.json` を編集しても
**そのままでは反映されない**（compose 側 [IADR-0079] と同じ運用差分）。runtime state 保持と realm 定義の再現性の
トレードオフを次で担保する:

1. **破壊的（推奨・realm を作り直してよい）**: `keycloak-data` PVC を消して Pod を再作成 → 空 PVC に `--import-realm`
   が最新 realm.json を再投入する。realm ConfigMap（単一情報源）は `k8s-local-up.sh` が実 realm ファイルから毎回生成する。
   ```bash
   kubectl -n platform-infra delete pvc keycloak-data
   kubectl -n platform-infra rollout restart deploy/keycloak   # 空 PVC 再作成 → realm 再 import
   ```
   実行時変更（管理コンソールで追加したユーザー等）は失われる。
2. **非破壊（runtime state 保持・部分反映）**: 管理コンソール（port-forward 後 `http://keycloak:8080`・admin/admin）
   または `kcadm` の partial import で当該変更のみ適用する。

## 機密情報（fail-safe 既定）

`k8s-local-up.sh` は未設定なら **dev 既定 / 空（no-op）** で Secret を作成する。実接続を有効化する場合のみ
環境変数で上書きする（値は Git に載せない）。

| 環境変数 | 生成 Secret / キー | 既定 | 用途 |
| --- | --- | --- | --- |
| `PG_PASSWORD` | `platform-infra/postgres.password` | `postgres` | Postgres 管理 |
| `RABBITMQ_USER` | `platform-infra/rabbitmq.username` | `guest` | RabbitMQ 利用者名（#1022。**helm の `global.messaging.user` と揃えること**） |
| `RABBITMQ_PASSWORD` | `platform-infra/rabbitmq.password` ＋ `microservices-platform/rabbitmq-app.password` | `guest` | RabbitMQ（#1022 でアプリ側 Secret を追加。ブローカと同値） |
| `KEYCLOAK_ADMIN_PASSWORD` | `platform-infra/keycloak-admin.password` | `admin` | Keycloak 管理 |
| `MINIO_ACCESS_KEY`/`MINIO_SECRET_KEY` | `microservices-platform/minio-credentials` | `minioadmin` | MinIO（chart 参照） |
| `WIKIJS_DB_PASSWORD` | `microservices-platform/wikijs-db.password` | `kp` | Wiki.js DB |
| `WIKIJS_SYNC_APIKEY` | `microservices-platform/wikijs-sync.apiKey` | 空→**bootstrap が発行**（#1108） | WikiService→Wiki.js 同期。**明示指定が無ければ `deploy/local/wikijs-setup/bootstrap.sh` が Wiki.js に発行させて書き戻す**。up の再実行では既存値を保つ（空で潰さない） |
| `WIKIJS_ADMIN_PASSWORD` | `microservices-platform/wikijs-admin.password` | **無し（乱数生成）** | Wiki.js の管理者（#1108 / [IADR-0327](../../.ai-context/adr/IADR-0327_wikijs-setup-bootstrap.md)）。**dev 既定文字列を置かない** —— エッジに露出する実ログイン口である |
| `ANTHROPIC_API_KEY` | `microservices-platform/llm-provider-credentials` | 空=呼ばない | MSP LLM Gateway（values-local が `Llm__ApiKey` へ配線） |

> `llm-provider-credentials` は values-local の `services.llmgateway.extraEnv` で LlmGateway の
> `Llm__ApiKey` に注入される（本ローカル環境のみ）。本番 chart（`deploy/helm` の `values.yaml`）は
> この Secret（`deploy/bootstrap/secret-templates.example.yaml` 由来）を未参照であり、本番側の配線は別課題。

AST 側の機密（`ANTHROPIC_API_KEY` / Finnhub / Discord / `Broker__Provider=paper`）は
AST#122 の chart で同様に fail-safe 既定で注入する。

## dev ログインユーザー（realm import・本番流用禁止）

realm import（`deploy/keycloak/microservices-platform-realm.json`）に含まれる開発専用ユーザーで
ログインできる（詳細は [`docs/security/security.md`](../../docs/security/security.md) の
「開発専用（dev-only）の平文認証情報」を参照）。

| ユーザー / パスワード | ロール・属性 | 用途 |
| --- | --- | --- |
| `developer` / `Developer-2026` | `platform-admin`+`platform-operator`+`wiki-editor`、clearance=`restricted` | 全機能を 1 アカウントで疎通確認する dev 用スーパーユーザー |
| `poc-user` / `Poc-Passwd2026` | ロール無し・ABAC 属性のみ（clearance=`internal`） | ABAC 属性ユーザーの検証 |
| `poc-operator` / `PocOperator-2026` | `platform-operator` のみ | 運用者ロールの検証 |

> ロール別の挙動差分（権限分離）を確認したい場合は `developer` ではなく `poc-*` を使うこと。
>
> **🔴 ［2026-08-28 変更 / #438］初回ログインで TOTP の登録画面が挟まる。** ADR-0026 は
> 「TOTP による MFA を必須」と確定しており、realm import で作られるこの 3 名にも
> `CONFIGURE_TOTP` を必須アクションとして持たせた。**認証アプリで QR を読むか、
> 画面に出ているシークレットを控えること。** 2 回目以降は毎回 6 桁の入力を求められる。
> 自動検証（`scripts/verify-oidc-edge-flow.sh`）はこの段を自分で通す（`scripts/lib/totp.js`）。
> **パスワードグラント（直接付与）は realm の全 client で無効である** ——
> 開けると MFA を迂回できる口になるため（`scripts/check-realm-constraints.js` の検査 5 が固定する）。
>
> **［2026-08-22 変更 / #780 第2段］パスワードを realm の `passwordPolicy`
> （`length(12)` ＋ 3-of-4 文字種）に適合する値へ変更した。** 旧パスワード（`developer` /
> `poc-password` / `poc-operator-password`）は realm 自身のポリシーに違反しており、
> **PVC が空の状態から `--import-realm` すると import 自体が失敗する**潜在バグだった
> （PVC 永続化で再 import が常にスキップされていたため無自覚に隠れていた。#780 第2段の
> realm 作り直しで顕在化・実測）。

## AST 統合スタック疎通（エッジ /bff・ブラウザ OIDC・Issue #284）

> 起点: [IADR-0076](../../.ai-context/adr/IADR-0076_edge-bff-routing-and-oidc-hostname.md) /
> 作業仕様書 [`.ai-context/specs/20260719_issue-284-live-integration-wiring.md`](../../.ai-context/specs/20260719_issue-284-live-integration-wiring.md)

### AST 3 画面系（AST/SC-01・AST/SC-02・AST/SC-03）の到達

> **AST chart の適用が前提**（Issue #407 / [IADR-0107](../../.ai-context/adr/IADR-0107_ast-owned-service-single-deployment.md)）

AST の 3 画面系サービス（`configuration` / `risk-management` / `market-monitor`）は
**AST chart（`ai-stock-trading` namespace）が単一の所有者**であり、MSP namespace には実体を置かない。
`values-local.yaml` はこれらを有効化しない（本番像 `values.yaml` の fail-safe 既定 `enabled: false` と一致）。

BFF の `/bff/assumptions`・`/bff/risk-controls/*`・`/bff/monitor/*`（#283/#287/#288）は、
`deploy/local/aliases/microservices-platform-externalnames.yaml` の **ExternalName alias** が
`<svc>-service` を `<svc>-service.ai-stock-trading.svc.cluster.local` へ解決させることで、
AST namespace の単一実体へ届く（BFF・本番像 values ともに無改修）。alias は `k8s-local-up.sh` の `[7/7]` が適用する。

```bash
# 到達確認（AST chart 適用後）
kubectl -n microservices-platform get svc risk-management-service -o jsonpath='{.spec.externalName}'
#   → risk-management-service.ai-stock-trading.svc.cluster.local
kubectl -n ai-stock-trading get pods -l app=risk-management-service
```

**AST chart 未適用時**: alias の解決先に Service が無いため、BFF は不達→**502 へ縮退**する（fail-safe。
[IADR-0071](../../.ai-context/adr/IADR-0071_ast-risk-controls-bff-integration.md) /
[IADR-0072](../../.ai-context/adr/IADR-0072_ast-monitor-bff-integration.md) の既存設計どおり、readiness の
`UriHealthCheck` には含めないため BFF 自体の可用性は落ちない）。3 画面を使うには AST chart を適用すること。

> **なぜ MSP 側に置かないか**: 以前は `values-local.yaml` が同 3 サービスを MSP namespace にも重複デプロイして
> いた。両 namespace の `rabbitmq` / `postgres` は ExternalName で**同一の `platform-infra` 実体**を指すため、
> `risk-management` / `market-monitor` の計 4 Pod が同一キュー `TradeDecisionMade` を **consumers=4** で
> 奪い合い、取引判断（`OrderApproved` / `OrderRejected`）を**無言で取りこぼした**（Issue #407 原因A）。
> 再発は `scripts/check-unit-service-ownership.js`（CI 必須チェック）が止める。

### エッジ /bff/* ルーティング

本番像は Istio `Gateway`/`VirtualService`（`edge.*`、`templates/edge.yaml`）で外部の `/bff/*` を `bff-service` へ
通す。経路B は Istio を導入しないため `values-local` で `edge.enabled=false`。経路B で `/bff/*` に到達するには
BFF を直接 port-forward する:

```bash
kubectl -n microservices-platform port-forward svc/bff-service 5080:8080
#   → http://localhost:5080/bff/...   （認証必須。匿名は 401）
```

### 送信メールの確認（開発環境の捕捉用 MTA。Issue #1144 / IADR-0344）

**開発環境から外部へメールは出ない。** 計画 ADR が「開発環境では実送信しない。捕捉用 MTA を置く」と
確定しているため、`platform-infra` に `mailpit` が **dev 既定**（opt-in ではない）で立ち、
Keycloak の realm もそこを送出先の既定にしている。**パスワードリセットのメールはここに溜まる。**

```bash
kubectl -n platform-infra port-forward svc/mailpit 8025:8025
#   → http://localhost:8025            （受信箱。認証は無い）
node scripts/check-password-reset-mail.js
#   → 申請 → 送出 → 受信 → 本文（リンクと有効期限のみ）を機械で確かめる
```

> **エッジ（50000）には出していない。** 受信箱の中身は**パスワードリセットリンク＝認証資格**であり、
> UI は認証を持たない。見るときは上のように**運用者が明示的に開く**。
>
> 実リレー（go-live のメールテナント）へ向けるのは、`SMTP_HOST` を明示したときだけである
> （[運用 Runbook](../../docs/operations/keycloak-smtp-relay-setup-runbook.md)）。

### SPA(/settings) 到達（Issue #313 / IADR-0078）

`values-local` は `frontend.enabled=true` で SPA(frontend) を k8s に配信する（`k8s-local-images.sh` が
`k3d-local/microservices-platform/frontend` を build/import・#275 MAPPING 登録済み）。経路B は `edge.enabled=false`
（Istio 未導入）のため、SPA へはエッジではなく `frontend-service` を直接 port-forward して到達する:

```bash
kubectl -n microservices-platform port-forward svc/frontend-service 8081:8080
#   → http://localhost:8081/            （SPA。/settings=SC-01/02/03）
#   → http://localhost:8081/bff/...     （nginx が in-cluster bff-service:8080 へプロキシ。BFF port-forward 不要）
```

> **ローカル port-forward のポートは realm の `platform-spa` に恒久登録済みの `8081` または `3100` を使う**（`redirectUris`=
> `http://localhost:{8081,3100}/*`・`webOrigins` 同左。ログアウト後リダイレクト `post.logout.redirect.uris` も両ポートを
> 登録済みで、値は Keycloak の複数値区切り `##` で連結する〔`http://localhost:3100/*##http://localhost:8081/*`〕・Issue #340）。
> SPA は `redirect_uri=<origin>/callback` を送るため、
> ブラウザで開く origin（＝上の port-forward のローカルポート）が `platform-spa` に登録されている必要がある。両ポートとも
> 登録済みのため、**ブラウザ OIDC で Keycloak 管理コンソールへの redirect URI 手動追加は不要**。別のローカルポートを
> 使いたい場合は、そのポートを `deploy/keycloak/microservices-platform-realm.json` の `platform-spa` に追記する（realm.json の
> 変更は**新規クラスタ作成時の realm import で反映**される。**既存のローカル環境**では管理コンソールで一度追加するか、
> `k3d cluster delete msp-ast-dev` → 再作成で realm を再 import して反映する。永続化 `PERSIST=1` 時の反映手順は上記
> 「realm を更新したときの反映手順」を参照）。

frontend pod の nginx が `/bff/*` を in-cluster の `bff-service:8080` へ内部プロキシするため、上の BFF port-forward
（5080）は SPA 経由では不要（`/bff` を直接叩いて確認したい場合のみ使う）。OIDC は下記 issuer 統一の**手順B**
（エッジ host 集約）に従う —— **issuer は `https://keycloak.localhost/realms/platform` である**
（IADR-0243・#780。`values-local.yaml` が `global.auth.metadataAddress`（in-cluster）と
`global.auth.validIssuers`（エッジ）を与えて backend を追随させる）。

> 本番像は `edge.enabled=true` で Istio VirtualService の catch-all（`/bff`・`/realms` の後）が SPA を
> `frontend-service` へ流し、`allow-edge-ingress-to-frontend` NetworkPolicy が default-deny 下の到達を許可する。
> 実ブラウザでの `/settings` 実表示・OIDC 実ログインは稼働 k3d 依存（本 issue の live 分・#284 手順）。

### Wiki.js の初期セットアップ（#1108）

`scripts/k8s-local-up.sh` は **既定で** [`wikijs-setup/bootstrap.sh`](wikijs-setup/README.md) を呼び、
Wiki.js の初期セットアップ・同期 API キー・本文 locale を冪等に入れる（opt-in ではない）。

🔴 **これが無いと Wiki.js は「`2/2 Running` なのに使えない」状態で残る。** Wiki.js 2.x は
セットアップが済むまで `/graphql` を載せず、その間 `server/setup.js` の catch-all が
**`/healthz` を含む全 URL に 200 を返す**ため、probe は通り Pod は Running のまま、
**Wiki 同期だけが全件エラーキューへ落ちる（画面には何も出ない）**。

セットアップ状態は共有 Postgres の `wikijs` DB に載る。`PERSIST=1` を付けずに立てたクラスタでは
`emptyDir` なので、**postgres Pod を作り直すと消える** —— そのときは bootstrap を再実行する（冪等）。
検知は `node scripts/check-stack-ready.js` の **G7**（fail-closed）。詳細は
[wikijs-setup/README.md](wikijs-setup/README.md)。

### Wiki 閲覧の到達（SC-04・Issue #344 → #1200）

SPA の「Wiki 閲覧」画面（SC-04）は、**BFF（`/bff/wiki/*`）→ WikiService（前段 ABAC ゲートウェイ）経由で
ページツリー・本文・検索結果を取得し、SPA の中に描く**（計画 ADR-0073 決定 2。口は #1199 /
[IADR-0355](../../.ai-context/adr/IADR-0355_bff-wiki-relay.md)、画面は #1200 /
[IADR-0365](../../.ai-context/adr/IADR-0365_sc04-wiki-screen-ledger-and-sanitize.md)）。Wiki.js 本体 UI への外部リンクは無く、
画面は `WIKI_BASE_URL` を**読まない**。利用者が Wiki の内容へ到達する経路は前段ゲートウェイの 1 本である（決定 1）。
本番像 `values.yaml` の `wikijs.ingress.enabled: false` はそのまま不変であり、**画面が BFF 経由になったことで本番でも
SC-04 が機能する**（従前は `WIKI_BASE_URL` 未設定で「接続先が未設定」となり機能していなかった）。

🔴 **dev では ABAC の統制が働かない。** 開発環境は Wiki.js 本体を**管理 UI**（OIDC 構成・ロケール導入・API キー発行）の
ために edge overlay（`LOCALEDGE=1`。`edge/admin-ingress-wiki.yaml` が `wiki.localhost:50000` を `wiki-js:3000` へ直結）で
**直接露出したまま**である（ADR-0073 決定 5・[IADR-0032](../../.ai-context/adr/IADR-0032_wikijs-dev-exposure-opt-in.md) 決定 1）。
この経路は WikiService を通らないため、**一覧・本文・検索のすべてが前段を迂回して読める**。「dev だから安全」ではなく
「**dev には統制が無い**」と読むこと（planning#286 裁定の型）。開発者が `wiki.localhost` で見た挙動を本番の挙動と読むと、
存在秘匿が効いていない画面を正常と誤認する。**本番の挙動（権限外は一覧に出ず本文は 404）を確かめるのは SPA の `/wiki` 側**である。

- **`WIKI_BASE_URL`（`values-local.yaml` の `frontend.extraEnv`）**: 画面はもう読まない。dev の管理 UI の到達先を示す値として
  残してある（決定 5。撤去は本 issue の射程外）。**本番像 `values.yaml` では設定しないことが統制**（決定 1。同ファイル
  `frontend.extraEnv` のコメント）であり、**機械検査は無く構成の規律**である。
- **Wiki.js 管理 UI（dev のみ）**: 既定（`LOCALEDGE=1`）は `https://wiki.localhost:50000`（他の管理ツールと同じく
  **edge 集約後の正規 URL**。[IADR-0091](../../.ai-context/adr/IADR-0091_local-edge-aggregation-traefik.md)・
  [edge/README](edge/README.md)「アクセス／OIDC（集約後 URL）」）。非 edge（port-forward）で使う場合:

```bash
kubectl -n microservices-platform port-forward svc/wiki-js 3300:3000
#   → http://localhost:3300/     （非 edge 利用時の管理 UI。Site URL も同値へ揃える）
```

- **Wiki.js の SSO（Keycloak OIDC）ログイン**（管理 UI 用）: Wiki.js は開いた後 Keycloak へリダイレクトするため、issuer 到達性は
  **手順A**（`hosts` に `127.0.0.1 keycloak` ＋ `port-forward svc/keycloak 8080:8080`）と同じく解く。realm `wiki-js`
  client は `https://wiki.localhost:50000/*`（edge 集約）と `http://localhost:3300/*`（**上記 k8s の port-forward 用**・#385）を
  登録済み。`http://localhost:3001/*` は compose(dev) の host 公開用（[IADR-0032](../../.ai-context/adr/IADR-0032_wikijs-dev-exposure-opt-in.md)）
  であり k8s の port-forward では使わない。非 edge で SSO を使う場合は Wiki.js の **Site URL も `http://localhost:3300`** に
  揃える（コールバックは `{Site URL}/login/{strategyKey}/callback`・[wiki-oidc/README](wiki-oidc/README.md)）。
  **ストラテジと Site URL の投入は自動である**（#1127・IADR-0342）——
  `WIKIJS_OIDC=1 bash scripts/k8s-local-up.sh`（既定オフの opt-in）か、既に立っているスタックなら
  `WIKIJS_OIDC=1 bash deploy/local/wikijs-setup/bootstrap.sh`。冪等で、2 回目は何も変えない。
  非 edge で使うときは同時に `WIKIJS_SITE_URL=http://localhost:3300` を渡す。
  実ブラウザでの SSO ログイン疎通は稼働 k3d・edge 設定依存＝**live**（本 issue の live 分）。
- 本番像 `values.yaml` の `frontend.extraEnv` は空のまま不変で、**`WIKI_BASE_URL` は供給しない**（ADR-0073 決定 1）。
  Wiki.js への直接到達は既定で塞ぐ運用（Ingress 既定 disabled・[IADR-0020](../../.ai-context/adr/IADR-0020_wiki-js-deployment-abac-gateway.md)）に従う。
  従前ここには「本番は実 Wiki URL を per-env の `extraEnv` で供給する（opt-in）」と書いてあったが、**それは統制を外す設定**であり
  #1200 で撤回した。

### ブラウザ OIDC の issuer 統一（原則と 2 手順）

**原則**: ブラウザが受け取る token の `iss` と、サービス側の検証基準が **同一 URL** でなければならない。

> 🔴 **［2026-08-31 / #780・IADR-0243］既定は手順B（エッジ host 集約）である。**
> issuer は **`https://keycloak.localhost/realms/platform`** であり、`deploy/local/infra/keycloak.yaml` の
> `KC_HOSTNAME_URL` がその単一情報源である。**`http://keycloak:8080` を issuer とする記述（手順A）は
> 過去の姿であり、いま実行すると `iss` が合わない。** 手順A の記述は経緯として残すが、追随しないこと。
> pod からエッジ host を引けるようにするのは `coredns-custom`（IADR-0227）で、**hosts 追記も
> port-forward も要らない**（`scripts/verify-oidc-edge-flow.sh` がその前提なしで完走する）。

- **手順A（過去の姿・2026-08-22 の IADR-0243 で既定から外れた）**: ブラウザに in-cluster 名を解決させる。
  1. hosts に `127.0.0.1 keycloak` を追記（Windows: `C:\Windows\System32\drivers\etc\hosts`）。
  2. `kubectl -n platform-infra port-forward svc/keycloak 8080:8080`。
  3. これで browser も cluster も `http://keycloak:8080` を issuer として共有する。SPA は compose の frontend
     （`http://localhost:3100`・既存 `platform-spa` origin）を使い、その `BFF_UPSTREAM` を上記 BFF port-forward
     （`http://localhost:5080`）へ、`OIDC_AUTHORITY` を `http://keycloak:8080/realms/platform` へ向ける。
     k8s 配信（#313）で確認する場合は上記「SPA(/settings) 到達」の `frontend-service` port-forward（`8081` または
     `3100`）を使う。**いずれのポートも `platform-spa` に恒久登録済み**（`http://localhost:{8081,3100}/*`・#340）のため、
     ブラウザ OIDC で管理コンソールへの redirect URI 手動追加は不要（手順A は per-session の realm 改変なしで成立する）。
  4. token 検証: 取得した access_token を base64url デコードし `iss` と `realm_access.roles`（`trading-owner`）を確認する。
- **手順B（単一エッジ host に集約する場合・任意）**: chart の `edge.oidc.enabled=true` で SPA/`/bff`/`/realms` を
  同一エッジ host に集約できる（`edge.oidc.host/port` で Keycloak を指す）。この場合のみ運用者が (i) その host を
  `platform-spa` の redirectUris/webOrigins へ追記、(ii) `global.auth.authority` を同 host へ上書き、(iii) in-cluster から
  同 host を解決させる。(iii) は稼働環境依存＝live。(iii) には次の 2 択がある。
  - **(iii-a) backend の metadata/issuer 分離（推奨・Issue #314 / [IADR-0086](../../.ai-context/adr/IADR-0086_oidc-issuer-metadata-split.md)）**:
    CoreDNS を触らず、backend の OIDC 検証で metadata 取得先（in-cluster）と issuer 検証値（エッジ host）を分離する。
    `global.auth.authority` は上書きせず（in-cluster 名のまま）、代わりに次を設定する:
    ```yaml
    global:
      auth:
        metadataAddress: http://keycloak:8080/realms/platform/.well-known/openid-configuration
        validIssuers: https://<edge-host>/realms/platform
    ```
    サービスは in-cluster の `metadataAddress` から署名鍵(JWKS)を取得し、エッジ host の `iss` を `validIssuers` で
    受理する。issuer 検証は弱めない（`ValidateIssuer=true` のまま・metadata 由来 issuer と併存＝手順A token も通る）。
    この場合 (ii) の `global.auth.authority` 上書きは不要（`metadataAddress` が metadata 取得を担う）。
  - **(iii-b) CoreDNS 追記**: 稼働クラスタの CoreDNS に「エッジ host → in-cluster サービス」の解決を追記する。
    環境ごとに壊れやすいため、(iii-a) が使えない構成向けの代替とする。

> 実ブラウザログイン end-to-end・Playwright E2E・Pod 実起動ヘルス緑は稼働 k3d 依存（本 issue の live 分・#284）。
> 手順B の単一エッジ host OIDC 実ログインも稼働環境（エッジ host 到達・`platform-spa` redirectUris 追記）依存＝live（#314）。

## Headlamp（k8s 管理 UI・Keycloak OIDC・Issue #271）

> 起点: [IADR-0080](../../.ai-context/adr/IADR-0080_headlamp-k8s-management-ui.md) /
> 作業仕様書 [`.ai-context/specs/20260719_issue-271_headlamp-k8s-management-ui.md`](../../.ai-context/specs/20260719_issue-271_headlamp-k8s-management-ui.md)
> ／apiserver OIDC 配線の結論（**適用不能**）: [IADR-0084](../../.ai-context/adr/IADR-0084_headlamp-oidc-apiserver-flags.md)
> の「⚠️ 2026-07-25 追記」／本節の根拠: [`.ai-context/specs/20260726_issue-328_headlamp-token-login-docs.md`](../../.ai-context/specs/20260726_issue-328_headlamp-token-login-docs.md)（#328・#388）

[Headlamp](https://headlamp.dev/)（CNCF Sandbox の k8s UI）を **opt-in** で導入し、Pod / Deployment / Service /
ログ等をブラウザから閲覧・操作できる。

> **ローカル（経路B）のログインは token 方式が正式手順である。**
> Keycloak OIDC ログインは **k8s の https-issuer 制約により現行では成立しない**。OIDC 化は
> **#388（全経路 HTTPS 化）と同時にのみ可能**であり、それまでは下記の
> `kubectl -n platform-infra create token headlamp-viewer` で発行した SA トークンを UI の **Token** 方式に貼る。

### 根本原因（なぜ OIDC ログインができないか）

Kubernetes 1.30 以降は、レガシーな `--oidc-*` フラグを内部で**構造化認証設定（`jwt[0]`）へ変換**し、
`issuer.url` に **https スキームを強制**する（`URL scheme must be https`。scheme の例外も insecure 用の逃げ道も無い）。
かつて経路B の Keycloak は [`deploy/local/infra/keycloak.yaml`](infra/keycloak.yaml) の
`KC_HOSTNAME_URL=http://keycloak:8080` により、realm が発行する token の `iss` が **http に固定**されていた。
apiserver が受理できる issuer（https）と realm が発行する issuer（http）が**両立し得なかった**ため、
apiserver 側にフラグを足しても OIDC ログインは成立しなかった。

> 🔴 **［2026-08-31 / #780］この前提は解消した。** `KC_HOSTNAME_URL` は
> **`https://keycloak.localhost`** であり、token の `iss` は https である（IADR-0243）。
> apiserver 側の OIDC 検証と issuer host の名前解決は **#781（IADR-0310）**が `APISERVER_OIDC=1` の
> opt-in として配線した。Headlamp のブラウザ OIDC ログインが成立することは #780 で実測済みである
> （`/oidc?cluster=main` → エッジの認可 → `/oidc-callback` → `headlamp-auth-main.0` cookie）。
> **ただし apiserver 側を有効にしていないクラスタでは、ログインは通ってもクラスタ API が 401 になる**
> ——「ログインできない」と「クラスタが見えない」は別の話である。下の token 方式は引き続き有効な代替である。

### 🚫 やってはいけないこと（クラスタが起動不能になる）

**`/etc/rancher/k3s/config.yaml.d/99-headlamp-oidc.yaml` のような apiserver OIDC ドロップインを置いて k3s を
再起動してはならない。** 実測（`k3s v1.35.4+k3s1`・Rancher Desktop 内蔵 k3s）では、kube-apiserver が

```
Error: invalid authentication configuration: jwt[0].issuer.url:
  Invalid value: "http://keycloak:8080/realms/platform": URL scheme must be https
```

で **19:46:33〜19:47:53 の間に 10 回連続で起動失敗し、クラスタが停止した**（ドロップインを外して再起動するまで
復旧しない）。過去に検証で作成したファイルが `/root/99-headlamp-oidc.yaml.disabled` として**無効化された状態で
退避**されている場合、**そのまま無効のまま置いておくこと**（`config.yaml.d/` へ戻さない・リネームしない）。

- **k3d 経路（対処済み）**: かつて `scripts/k8s-local-up.sh` は `HEADLAMP_OIDC_APISERVER` 未設定時に `HEADLAMP` の値へ
  追従して同じ 4 フラグを `k3d cluster create` へ付与しており、`HEADLAMP=1` だけで上記の起動失敗を踏んだ。
  この経路は [IADR-0105](../../.ai-context/adr/IADR-0105_remove-apiserver-oidc-flag-wiring.md)（#399）で**除去済み**で、
  現在は `HEADLAMP=1` のみで安全に実行できる（回避用の `HEADLAMP_OIDC_APISERVER=0` の併記は**不要**。
  指定しても no-op）。Rancher Desktop 経路（内蔵 k3s）はスクリプトがクラスタを作らないため元々対象外。

### 有効化（opt-in・既定オフ）

```bash
HEADLAMP=1 bash scripts/k8s-local-up.sh   # Rancher Desktop（内蔵 k3s）・k3d 共通
# → deploy/local/headlamp（Deployment/Service ＋ Pod 用 SA `headlamp` ＋ token ログイン用 SA `headlamp-viewer`
#   と閲覧専用 RBAC・#398/IADR-0108）を適用。
#   OIDC client secret は Secret headlamp-oidc（platform-infra）へ dev 既定で作成（HEADLAMP_OIDC_CLIENT_SECRET で上書き可）。
```

Headlamp UI へは `LOCALEDGE=1` なら `https://headlamp.localhost:50000`、単独なら port-forward で到達する:

```bash
kubectl -n platform-infra port-forward svc/headlamp 4466:80   # http://localhost:4466
```

### ログイン（正式手順・token 方式）

UI の認証方式で **Token** を選び、`headlamp-viewer` ServiceAccount の短命トークンを貼る:

```bash
kubectl -n platform-infra create token headlamp-viewer --duration=24h
```

`headlamp-viewer` は overlay に収録済みのため（[`headlamp-viewer-rbac.yaml`](headlamp/headlamp-viewer-rbac.yaml)・
#398 / [IADR-0108](../../.ai-context/adr/IADR-0108_headlamp-viewer-readonly-rbac.md)）、`HEADLAMP=1` で up した直後から
**手動作成なしに**トークンを発行できる。

**権限は閲覧専用**（`get`/`list`/`watch` のみ）である。内訳は組み込み ClusterRole `view`（全 namespace の
リソース読み取り。**`secrets` は含まない**）＋ `headlamp-viewer-cluster-read`（Node / PV / StorageClass / CRD /
RBAC 等のクラスタスコープ資源の読み取り）。UI からの scale・delete・exec・YAML 編集は **403** になる（意図どおり）。
書き込みが必要な操作は各自の kubeconfig で `kubectl` を使う。

> 従来この SA を手作りしていたクラスタには、`cluster-admin` を束ねた CRB `headlamp-viewer` が残っている場合が
> ある。名前が異なる（overlay 側は `headlamp-viewer-view` / `headlamp-viewer-cluster-read`）ため衝突はしないが、
> 残存すると実効権限は cluster-admin のままである。閲覧専用に揃えるなら手作り分を消す:
> `kubectl delete clusterrolebinding headlamp-viewer --ignore-not-found`

fail-safe: Headlamp **Pod** が使う ServiceAccount（`headlamp`）には広域権限を bind していないため、トークンを
貼らない限りクラスタは可視化できない（[IADR-0080](../../.ai-context/adr/IADR-0080_headlamp-k8s-management-ui.md)）。
`headlamp-viewer` は Pod に割り当てず、トークンは Secret として常駐しない都度発行の短命トークンである。

### #388 で OIDC 化するときにそのまま効く資産（現状は inert・無害）

以下は**すでに恒久化済み**で、#388 で apiserver が OIDC を受理できるようになった時点で**そのまま機能する**。
現行では `oidc:` 接頭辞の identity が生成されないため単に無効（inert）であり、放置して害はない。

- realm client `headlamp` の claim mapper **`headlamp-realm-roles`**（realm ロールを `groups` クレームへ発行）＝
  [`deploy/keycloak/microservices-platform-realm.json`](../keycloak/microservices-platform-realm.json)（#389 /
  [IADR-0103](../../.ai-context/adr/IADR-0103_local-sso-persistence-and-claim-design.md)）。
- ClusterRoleBinding **`headlamp-developer-cluster-admin`**（User `oidc:developer` → `cluster-admin`）＝
  [`deploy/local/headlamp/headlamp.yaml`](headlamp/headlamp.yaml)（#271 / IADR-0080）。
- realm client `headlamp` の redirectUris（`http://localhost:4466/*` ＋ 集約後 `https://headlamp.localhost:50000/*`・#377）。

**cluster-admin を得られるのは `developer` だけ**である点に注意する。上記 bind の subject は User `oidc:developer`
のみで、`admin`（[IADR-0103](../../.ai-context/adr/IADR-0103_local-sso-persistence-and-claim-design.md) で realm に追加した
管理者ユーザー）に対応する bind は**存在しない**。`admin` でも入れるようにするか（`groups` クレーム由来の Group
subject を bind する等）は #388 で決める設計事項であり、本 PR 時点では未決である。

> **#388 完了後の live 受け入れ**（#271 / #328 の当初要求）: issuer を https へ統一し、apiserver に `oidc-ca-file` を
> 含めて再配線したうえで、ブラウザから **`developer`**（既存 bind に一致するユーザー）でログインし
> Pod/Deployment/Service/ログが閲覧できること。`developer` は dev スーパーユーザー（IADR-0066）で、ロール別の
> 権限分離検証には使わない（`poc-*` の役割）。

## 既知の制約

- **観測 UI は非同梱**: otel-collector は dev では `debug` エクスポータのみ（Prometheus/Tempo/Loki/Grafana は
  立てない）。UI が要るなら compose（`deploy/docker-compose.yml`）を併用する。
- **永続化は opt-in**: 既定の infra は emptyDir（Pod 再起動で再 init。dev 用途の割り切り）。`PERSIST=1` で
  Keycloak/Postgres/Qdrant を、`OBSERVABILITY=1` を併用すれば Prometheus/Loki/Tempo/Grafana も PVC 永続化できる
  （上記「永続化」節・IADR-0082 / IADR-0210）。
- **Istio/mTLS/NetworkPolicy/HPA/エッジ Gateway は無効**（values-local。`edge.enabled=false`）。本番像（STRICT mTLS・
  エッジ `/bff/*` ルーティング等）は不変。経路B の `/bff` 到達は BFF の port-forward で代替する（上記手順）。

## 手動でステップ実行する場合

```bash
# 事前に infra secrets・realm ConfigMap・テーマ ConfigMap を作成（k8s-local-up.sh の [3/7] が自動化する部分）
kubectl create namespace platform-infra
kubectl create secret generic postgres -n platform-infra --from-literal=password=postgres
kubectl create secret generic rabbitmq -n platform-infra --from-literal=username=guest --from-literal=password=guest
kubectl create secret generic keycloak-admin -n platform-infra --from-literal=password=admin
kubectl create configmap keycloak-realms -n platform-infra \
  --from-file=microservices-platform-realm.json=deploy/keycloak/microservices-platform-realm.json

# #438: realm.json の loginTheme/accountTheme=platform を解決するテーマ実体
# （k8s-local-up.sh 経由なら自動生成される。ここは手動でステップ実行する場合の再現用）。
# 作成しないまま Pod を起動しても keycloak.yaml 側は optional: true のため落ちないが、
# その場合ログイン画面が「テーマが見つからない」で 500 になる。
kubectl create configmap keycloak-theme-platform -n platform-infra \
  --from-file=login-theme-properties=deploy/keycloak/themes/platform/login/theme.properties \
  --from-file=login-css=deploy/keycloak/themes/platform/login/resources/css/platform.css \
  --from-file=account-theme-properties=deploy/keycloak/themes/platform/account/theme.properties \
  --from-file=account-css=deploy/keycloak/themes/platform/account/resources/css/platform.css \
  --dry-run=client -o yaml | kubectl apply -f -

kubectl apply -k deploy/local/infra                                  # infra
helm upgrade --install msp deploy/helm/microservices-platform \
  -n microservices-platform --create-namespace -f deploy/local/values-local.yaml
kubectl apply -f deploy/local/aliases/microservices-platform-externalnames.yaml
```
