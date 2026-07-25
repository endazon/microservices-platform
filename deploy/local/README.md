# ローカル k8s(k3d) dev 環境（MSP + AST 連結）

> 起点: [IADR-0066](../../docs/adr/IADR-0066_local-k8s-dev-environment.md) /
> 作業仕様書 [`docs/specs/20260713_issue-266_local-k8s-dev-env.md`](../../docs/specs/20260713_issue-266_local-k8s-dev-env.md) /
> Issue #266（MSP）・ai-stock-trading#122（AST chart）・#121（K8s CronJob）

本ディレクトリは **dev 専用**の資産である。本番像（[`deploy/helm`](../helm) 本体・
[`deploy/docker-compose.yml`](../docker-compose.yml)・[`deploy/argocd`](../argocd)）は変更しない。

## 何を作るか

```
[k3d cluster: msp-ast-dev]
  ns platform-infra          postgres / rabbitmq / redis / keycloak / qdrant / otel-collector   ← deploy/local/infra
  ns microservices-platform  既存 Helm chart（values-local: mesh/NP/HPA off, registry=local）
                             + ExternalName エイリアス（素のサービス名 → platform-infra）
  ns ai-stock-trading        AST chart（ai-stock-trading#122 で追加）
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

### opt-in オーバーレイ（可観測性 / Vault / GitOps・AST #24 / IADR-0077）

既定は**無効**（env 未設定で従来どおり）。env ゲートで追加のみ有効化する（既存ステップは不変）。

```bash
OBSERVABILITY=1 bash scripts/k8s-local-up.sh   # Prometheus/Loki/Tempo/Grafana + collector forwarding
VAULT=1         bash scripts/k8s-local-up.sh   # Vault dev + ClusterSecretStore(vault-backend)（要 ESO CRD）
ARGOCD=1        bash scripts/k8s-local-up.sh   # ArgoCD install + Application 適用（MSP/AST）
PERSIST=1       bash scripts/k8s-local-up.sh   # Keycloak(realm+runtime state)/Postgres を PVC 永続化（下記「永続化」節）
LOCALEDGE=1     bash scripts/k8s-local-up.sh   # ローカルエッジ集約: platform フロント 80/443 ＋ 管理ツール 50000（下記 edge 節）
ESO=1           bash scripts/k8s-local-up.sh   # Vault＋ESO で secret 自動供給（要 VAULT=1・本番同等 k8s auth・IADR-0096・#310）
```

- [`deploy/local/observability/README.md`](observability/README.md) — 可観測性スタック（既定 debug-only を維持）
- [`deploy/local/vault/README.md`](vault/README.md) — Vault dev + External Secrets（**dev 専用・平文秘密なし**）。`ESO=1` で secret 自動供給（[eso/README](vault/eso/README.md)・IADR-0096）
- [`deploy/local/argocd/README.md`](argocd/README.md) — GitOps ブートストラップ
- [`deploy/local/edge/README.md`](edge/README.md) — **ローカルエッジ集約**（platform 80/443 ＋ 管理ツール 50000・ホスト名ベース・IADR-0091）。**k3d はポート再作成が必要**（同 README のユーザー手順）
- Hetzner 実 stand-up・本番 NFR は **Tier 3**（対象外）。

### 永続化（opt-in・PERSIST=1・Issue #324 / IADR-0082）

> 起点: [IADR-0082](../../docs/adr/IADR-0082_local-k8s-infra-persistence.md) /
> 作業仕様書 [`docs/specs/20260719_issue-324_infra-persistence-k8s.md`](../../docs/specs/20260719_issue-324_infra-persistence-k8s.md)

既定の経路B infra は [IADR-0066](../../docs/adr/IADR-0066_local-k8s-dev-environment.md) の割り切りで `emptyDir`
（Pod 再起動で再 init）である。このため **Keycloak Pod が再起動するたびに realm が再 import され、管理コンソールで
加えた runtime state（追加ユーザー・シークレット・セッション等）が失われる**。`PERSIST=1` を付けると
[`deploy/local/infra-persistence`](infra-persistence/) オーバーレイが適用され、**Keycloak / Postgres を
`local-path` PVC で永続化**する（Pod 再起動でも状態を保持）。

```bash
PERSIST=1 bash scripts/k8s-local-up.sh
# → deploy/local/infra-persistence を適用（base infra + PVC 2 本 + volume パッチ）。
#   PVC: keycloak-data(1Gi, /opt/keycloak/data=file H2) / postgres-data(2Gi, /var/lib/postgresql/data)。
```

| サービス | PVC | マウント | 保持されるもの |
| --- | --- | --- | --- |
| Keycloak | `keycloak-data`（1Gi・local-path） | `/opt/keycloak/data`（`start-dev` の file H2） | realm ＋ runtime state（追加ユーザー・シークレット・セッション） |
| Postgres | `postgres-data`（2Gi・local-path） | `/var/lib/postgresql/data` | 全アプリ DB（MSP + AST） |

- **保持されるのは Pod の再起動/再作成の範囲**。`bash scripts/k8s-local-down.sh` は k3d 経路ではクラスタごと、
  Rancher Desktop 経路では `platform-infra` namespace を削除するため、**`down`→`up` の再構築サイクルでは PVC
  （`keycloak-data`/`postgres-data`）も消える**（= realm/DB は再生成）。PVC を残したまま作り直したいときは `down` を
  使わず `kubectl -n platform-infra rollout restart deploy/keycloak deploy/postgres` 等で Pod のみ入れ替える。
- **既定（`PERSIST` 未設定）は従来どおり `emptyDir`**（挙動不変・後方互換・fail-safe）。`local-path` 等の
  provisioner が無いクラスタでも既定経路は Pod Pending 化しない。qdrant / rabbitmq / redis / otel は emptyDir 継続
  （embeddings は再生成可・queue/cache は揮発前提・stateless。詳細は IADR-0082）。
- **⚠️ 既存環境の移行**: 途中から `PERSIST=1` に切り替えると Deployment の volume が差し替わりローリング更新が走る。
  **初回は空 PVC のため realm/DB は import/init で再生成**される（既存 emptyDir のデータは元々 Pod 生存期間のみの揮発
  データで、失う恒久データは無い）。以後の再起動では PVC のデータが保持される。リセットしたいときは PVC を消す:
  ```bash
  kubectl -n platform-infra delete pvc keycloak-data postgres-data   # 次回 PERSIST=1 起動で空から再生成
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
| `RABBITMQ_PASSWORD` | `platform-infra/rabbitmq.password` | `guest` | RabbitMQ |
| `KEYCLOAK_ADMIN_PASSWORD` | `platform-infra/keycloak-admin.password` | `admin` | Keycloak 管理 |
| `MINIO_ACCESS_KEY`/`MINIO_SECRET_KEY` | `microservices-platform/minio-credentials` | `minioadmin` | MinIO（chart 参照） |
| `WIKIJS_DB_PASSWORD` | `microservices-platform/wikijs-db.password` | `kp` | Wiki.js DB |
| `WIKIJS_SYNC_APIKEY` | `microservices-platform/wikijs-sync.apiKey` | 空 | WikiService→Wiki.js 同期 |
| `ANTHROPIC_API_KEY` | `microservices-platform/llm-provider-credentials` | 空=呼ばない | MSP LLM Gateway（values-local が `Llm__ApiKey` へ配線） |

> `llm-provider-credentials` は values-local の `services.llmgateway.extraEnv` で LlmGateway の
> `Llm__ApiKey` に注入される（本ローカル環境のみ）。本番 chart（`deploy/helm` の `values.yaml`）は
> この Secret（`deploy/bootstrap/secret-templates.example.yaml` 由来）を未参照であり、本番側の配線は別課題。

AST 側の機密（`ANTHROPIC_API_KEY` / Finnhub / Discord / `Broker__Provider=paper`）は
ai-stock-trading#122 の chart で同様に fail-safe 既定で注入する。

## dev ログインユーザー（realm import・本番流用禁止）

realm import（`deploy/keycloak/microservices-platform-realm.json`）に含まれる開発専用ユーザーで
ログインできる（詳細は [`docs/security/security.md`](../../docs/security/security.md) の
「開発専用（dev-only）の平文認証情報」を参照）。

| ユーザー / パスワード | ロール・属性 | 用途 |
| --- | --- | --- |
| `developer` / `developer` | `platform-admin`+`platform-operator`+`wiki-editor`、clearance=`restricted` | 全機能を 1 アカウントで疎通確認する dev 用スーパーユーザー |
| `poc-user` / `poc-password` | ロール無し・ABAC 属性のみ（clearance=`internal`） | ABAC 属性ユーザーの検証 |
| `poc-operator` / `poc-operator-password` | `platform-operator` のみ | 運用者ロールの検証 |

> ロール別の挙動差分（権限分離）を確認したい場合は `developer` ではなく `poc-*` を使うこと。

## AST 統合スタック疎通（エッジ /bff・ブラウザ OIDC・Issue #284）

> 起点: [IADR-0076](../../docs/adr/IADR-0076_edge-bff-routing-and-oidc-hostname.md) /
> 作業仕様書 [`docs/specs/20260719_issue-284-live-integration-wiring.md`](../../docs/specs/20260719_issue-284-live-integration-wiring.md)

### AST 3 サービスの有効化（自動）

`values-local.yaml` は AST の 3 画面系サービス（`configuration` / `risk-management` / `market-monitor`）を
**経路B で自動有効化**する（本番像 `values.yaml` は fail-safe の disabled のまま不変）。接続情報は
`values-local` の `extraEnv` が注入する（DB は経路B postgres の owner=ai に合わせ `ai/ai`、RabbitMQ は `guest/guest`）。
イメージは `k8s-local-images.sh` が MAPPING から 3 サービスとも build/import する。BFF の `/bff/assumptions`・
`/bff/risk-controls/*`・`/bff/monitor/*` はこれらへプロキシする（#283/#287/#288）。

### エッジ /bff/* ルーティング

本番像は Istio `Gateway`/`VirtualService`（`edge.*`、`templates/edge.yaml`）で外部の `/bff/*` を `bff-service` へ
通す。経路B は Istio を導入しないため `values-local` で `edge.enabled=false`。経路B で `/bff/*` に到達するには
BFF を直接 port-forward する:

```bash
kubectl -n microservices-platform port-forward svc/bff-service 5080:8080
#   → http://localhost:5080/bff/...   （認証必須。匿名は 401）
```

### SPA(/settings) 到達（Issue #313 / IADR-0078）

`values-local` は `frontend.enabled=true` で SPA(frontend) を k8s に配信する（`k8s-local-images.sh` が
`k3d-local/microservices-platform/frontend` を build/import・#275 MAPPING 登録済み）。経路B は `edge.enabled=false`
（Istio 未導入）のため、SPA へはエッジではなく `frontend-service` を直接 port-forward して到達する:

```bash
kubectl -n microservices-platform port-forward svc/frontend-service 8081:8080
#   → http://localhost:8081/            （SPA。/settings=SC-01/02/03）
#   → http://localhost:8081/bff/...     （nginx が in-cluster bff-service:8080 へプロキシ。BFF port-forward 不要）
```

> **ローカル port-forward のポートは realm の `spa-web` に恒久登録済みの `8081` または `3100` を使う**（`redirectUris`=
> `http://localhost:{8081,3100}/*`・`webOrigins` 同左。ログアウト後リダイレクト `post.logout.redirect.uris` も両ポートを
> 登録済みで、値は Keycloak の複数値区切り `##` で連結する〔`http://localhost:3100/*##http://localhost:8081/*`〕・Issue #340）。
> SPA は `redirect_uri=<origin>/callback` を送るため、
> ブラウザで開く origin（＝上の port-forward のローカルポート）が `spa-web` に登録されている必要がある。両ポートとも
> 登録済みのため、**ブラウザ OIDC で Keycloak 管理コンソールへの redirect URI 手動追加は不要**。別のローカルポートを
> 使いたい場合は、そのポートを `deploy/keycloak/microservices-platform-realm.json` の `spa-web` に追記する（realm.json の
> 変更は**新規クラスタ作成時の realm import で反映**される。**既存のローカル環境**では管理コンソールで一度追加するか、
> `k3d cluster delete msp-ast-dev` → 再作成で realm を再 import して反映する。永続化 `PERSIST=1` 時の反映手順は上記
> 「realm を更新したときの反映手順」を参照）。

frontend pod の nginx が `/bff/*` を in-cluster の `bff-service:8080` へ内部プロキシするため、上の BFF port-forward
（5080）は SPA 経由では不要（`/bff` を直接叩いて確認したい場合のみ使う）。OIDC は下記 issuer 統一の**手順A**に
従う（browser も cluster も `http://keycloak:8080` を issuer として共有する。`values-local` は OIDC を上書きせず
base 既定 `http://keycloak:8080/realms/microservices-platform` のまま＝backend の `Auth__Authority` と一致）。

> 本番像は `edge.enabled=true` で Istio VirtualService の catch-all（`/bff`・`/realms` の後）が SPA を
> `frontend-service` へ流し、`allow-edge-ingress-to-frontend` NetworkPolicy が default-deny 下の到達を許可する。
> 実ブラウザでの `/settings` 実表示・OIDC 実ログインは稼働 k3d 依存（本 issue の live 分・#284 手順）。

### Wiki 閲覧の到達（SC-04・Issue #344）

SPA の「Wiki 閲覧」画面（SC-04）は、ブラウザ向け実行時 config `wikiBaseUrl`（config.js の `WIKI_BASE_URL`）から
社内 Wiki（Wiki.js）を**新規タブで直接開く**導線である（BFF 経由ではない）。`WIKI_BASE_URL` が未設定（空文字）だと
画面は「**Wiki の接続先が未設定です**」と表示してリンクを出さない。経路B は `values-local.yaml` の
`frontend.extraEnv` で `WIKI_BASE_URL` を供給する。到達は他の管理ツール（grafana/minio/vault 等）と同じく
**edge 集約後の正規 URL** に整合させる（[IADR-0091](../../docs/adr/IADR-0091_local-edge-aggregation-traefik.md) の
edge overlay `deploy/local/edge`・`LOCALEDGE=1` で Wiki.js を `wiki.localhost:50000` に公開。realm `wiki-js` client も
同 URL 登録済み。[edge/README](edge/README.md)「アクセス／OIDC（集約後 URL）」）:

- **既定（`LOCALEDGE=1`）**: `values-local.yaml` は `WIKI_BASE_URL=http://wiki.localhost:50000` を供給する。SPA を
  `http://localhost/`（edge のフロント）で開き、「Wiki 閲覧」→「Wiki を開く」で `http://wiki.localhost:50000` が開く。
- **非 edge（`LOCALEDGE` 未使用・port-forward）で使う場合**: `values-local.yaml` の `WIKI_BASE_URL` を
  `http://localhost:3300` へ **override** し、`wiki-js` を port-forward する（値と port-forward を揃えること。
  不一致だと到達しない）:

```bash
kubectl -n microservices-platform port-forward svc/wiki-js 3300:3000
#   → http://localhost:3300/     （非 edge 利用時の override 先。WIKI_BASE_URL も同値へ）
```

- **Wiki.js の SSO（Keycloak OIDC）ログイン**: Wiki.js は開いた後 Keycloak へリダイレクトするため、issuer 到達性は
  **手順A**（`hosts` に `127.0.0.1 keycloak` ＋ `port-forward svc/keycloak 8080:8080`）と同じく解く。realm `wiki-js`
  client は `http://wiki.localhost:50000/*`（および port-forward 用 `http://localhost:3001/*`）を登録済みで、
  実ブラウザでの SSO ログイン疎通は稼働 k3d・edge 設定依存＝**live**（本 issue の live 分）。
- 本番像 `values.yaml` の `frontend.extraEnv` は空のまま不変。本番は実 Wiki URL を per-env の `extraEnv` で供給する
  （opt-in・後方互換）。Wiki.js への直接到達は既定で塞ぐ運用（Ingress 既定 disabled・[IADR-0020](../../docs/adr/IADR-0020_wiki-js-deployment-abac-gateway.md)）に従う。

### ブラウザ OIDC の issuer 統一（原則と 2 手順）

**原則**: ブラウザが受け取る token の `iss` と、サービス側の検証基準（`Auth__Authority`）が **同一 URL** で
なければならない。issuer は in-cluster 正準名 `http://keycloak:8080` に固定している（サービス間 JWT 用）。

- **手順A（推奨・realm/manifest 無改変）**: ブラウザに同じ in-cluster 名を解決させる。
  1. hosts に `127.0.0.1 keycloak` を追記（Windows: `C:\Windows\System32\drivers\etc\hosts`）。
  2. `kubectl -n platform-infra port-forward svc/keycloak 8080:8080`。
  3. これで browser も cluster も `http://keycloak:8080` を issuer として共有する。SPA は compose の frontend
     （`http://localhost:3100`・既存 `spa-web` origin）を使い、その `BFF_UPSTREAM` を上記 BFF port-forward
     （`http://localhost:5080`）へ、`OIDC_AUTHORITY` を `http://keycloak:8080/realms/microservices-platform` へ向ける。
     k8s 配信（#313）で確認する場合は上記「SPA(/settings) 到達」の `frontend-service` port-forward（`8081` または
     `3100`）を使う。**いずれのポートも `spa-web` に恒久登録済み**（`http://localhost:{8081,3100}/*`・#340）のため、
     ブラウザ OIDC で管理コンソールへの redirect URI 手動追加は不要（手順A は per-session の realm 改変なしで成立する）。
  4. token 検証: 取得した access_token を base64url デコードし `iss` と `realm_access.roles`（`trading-owner`）を確認する。
- **手順B（単一エッジ host に集約する場合・任意）**: chart の `edge.oidc.enabled=true` で SPA/`/bff`/`/realms` を
  同一エッジ host に集約できる（`edge.oidc.host/port` で Keycloak を指す）。この場合のみ運用者が (i) その host を
  `spa-web` の redirectUris/webOrigins へ追記、(ii) `global.auth.authority` を同 host へ上書き、(iii) in-cluster から
  同 host を解決させる。(iii) は稼働環境依存＝live。(iii) には次の 2 択がある。
  - **(iii-a) backend の metadata/issuer 分離（推奨・Issue #314 / [IADR-0086](../../docs/adr/IADR-0086_oidc-issuer-metadata-split.md)）**:
    CoreDNS を触らず、backend の OIDC 検証で metadata 取得先（in-cluster）と issuer 検証値（エッジ host）を分離する。
    `global.auth.authority` は上書きせず（in-cluster 名のまま）、代わりに次を設定する:
    ```yaml
    global:
      auth:
        metadataAddress: http://keycloak:8080/realms/microservices-platform/.well-known/openid-configuration
        validIssuers: https://<edge-host>/realms/microservices-platform
    ```
    サービスは in-cluster の `metadataAddress` から署名鍵(JWKS)を取得し、エッジ host の `iss` を `validIssuers` で
    受理する。issuer 検証は弱めない（`ValidateIssuer=true` のまま・metadata 由来 issuer と併存＝手順A token も通る）。
    この場合 (ii) の `global.auth.authority` 上書きは不要（`metadataAddress` が metadata 取得を担う）。
  - **(iii-b) CoreDNS 追記**: 稼働クラスタの CoreDNS に「エッジ host → in-cluster サービス」の解決を追記する。
    環境ごとに壊れやすいため、(iii-a) が使えない構成向けの代替とする。

> 実ブラウザログイン end-to-end・Playwright E2E・Pod 実起動ヘルス緑は稼働 k3d 依存（本 issue の live 分・#284）。
> 手順B の単一エッジ host OIDC 実ログインも稼働環境（エッジ host 到達・`spa-web` redirectUris 追記）依存＝live（#314）。

## Headlamp（k8s 管理 UI・Keycloak OIDC・Issue #271）

> 起点: [IADR-0080](../../docs/adr/IADR-0080_headlamp-k8s-management-ui.md) /
> 作業仕様書 [`docs/specs/20260719_issue-271_headlamp-k8s-management-ui.md`](../../docs/specs/20260719_issue-271_headlamp-k8s-management-ui.md)
> ／apiserver OIDC 配線: [IADR-0084](../../docs/adr/IADR-0084_headlamp-oidc-apiserver-flags.md) /
> [`docs/specs/20260719_issue-328_headlamp-oidc-apiserver-wiring.md`](../../docs/specs/20260719_issue-328_headlamp-oidc-apiserver-wiring.md)（#328）

[Headlamp](https://headlamp.dev/)（CNCF Sandbox の k8s UI）を **opt-in** で導入し、Pod / Deployment / Service /
ログ等をブラウザから閲覧・操作できる。ログインは既存 Keycloak（OIDC）で行い、`developer` / `developer` を流用する
（新たな認証情報を作らない＝アカウントは Keycloak が一元管理）。

### 有効化（opt-in・既定オフ）

```bash
HEADLAMP=1 bash scripts/k8s-local-up.sh
# → deploy/local/headlamp（ServiceAccount/Deployment/Service/ClusterRoleBinding）を適用。
#   OIDC client secret は Secret headlamp-oidc（platform-infra）へ dev 既定で作成（HEADLAMP_OIDC_CLIENT_SECRET で上書き可）。
```

Headlamp UI へは port-forward で到達する:

```bash
kubectl -n platform-infra port-forward svc/headlamp 4466:80   # http://localhost:4466
```

### ブラウザ OIDC ログイン（手順A ＋ apiserver OIDC フラグ）

Headlamp はブラウザから OIDC を行うため、issuer 到達性を上記 **手順A** と同じ方法で解く（realm/manifest 無改変）:

1. hosts に `127.0.0.1 keycloak` を追記（既に手順A で追記済みならそのまま）。
2. `kubectl -n platform-infra port-forward svc/keycloak 8080:8080`。
3. これで browser（Headlamp のリダイレクト先）も cluster も issuer `http://keycloak:8080` を共有する。
   realm client `headlamp` の redirectUris は `http://localhost:4466/*`（callback = `/oidc-callback`）。

さらに、**Headlamp が委譲する id_token を k8s API server が検証**するには、クラスタを OIDC 用 apiserver フラグ付きで
(再)作成する必要がある（apiserver 引数は**作成時のみ有効＝既存クラスタには後付けできず再作成**）。

#### k3d（`k8s-local-up.sh` が自動配線・#328 / IADR-0084）

`HEADLAMP=1 bash scripts/k8s-local-up.sh` で**新規**クラスタを作成すると、`k3d cluster create` に以下の apiserver OIDC
フラグが自動付与される（`HEADLAMP_OIDC_APISERVER` は既定で `HEADLAMP` に追従。既定オフ時の cluster create は現行と
バイト等価）。手動で組む場合の等価コマンドは:

```bash
k3d cluster create msp-ast-dev --agents 1 \
  -p "8080:80@loadbalancer" -p "8443:443@loadbalancer" \
  --k3s-arg "--kube-apiserver-arg=oidc-issuer-url=http://keycloak:8080/realms/microservices-platform@server:0" \
  --k3s-arg "--kube-apiserver-arg=oidc-client-id=headlamp@server:0" \
  --k3s-arg "--kube-apiserver-arg=oidc-username-claim=preferred_username@server:0" \
  --k3s-arg "--kube-apiserver-arg=oidc-username-prefix=oidc:@server:0"
```

- **既存クラスタを OIDC 付きに切り替える**には、`k3d cluster delete msp-ast-dev` してから `HEADLAMP=1 bash
  scripts/k8s-local-up.sh` で作り直す（フラグは後付け不可。スクリプトは reuse 時に再作成を促す WARN を出す）。
- 上書き env: `HEADLAMP_OIDC_ISSUER_URL` / `HEADLAMP_OIDC_CLIENT_ID`（既定は上記）。`HEADLAMP_OIDC_APISERVER=0` で
  `HEADLAMP=1` でもフラグを付けない（既存クラスタ再利用時の escape-hatch）。

#### Rancher Desktop（内蔵 k3s）

`k8s-local-up.sh` は Rancher の k8s を**作成しない**（既存 context を使う）ため、apiserver フラグはスクリプトからは
付与できない。内蔵 k3s へ同じ 4 引数を与えるには、Rancher Desktop の **override 設定**（`~/.config/rancher-desktop/`
配下の `k8s.override.yaml`。UI では Preferences → Kubernetes の追加引数）に k3s の `kube-apiserver-arg` として設定して
から Kubernetes を再起動する。値は上記 k3d の 4 フラグと同一（`@server:0` は不要）:

```yaml
# Rancher Desktop override（例）: k3s に apiserver OIDC 引数を与える
k3s:
  additionalArgs:
    - --kube-apiserver-arg=oidc-issuer-url=http://keycloak:8080/realms/microservices-platform
    - --kube-apiserver-arg=oidc-client-id=headlamp
    - --kube-apiserver-arg=oidc-username-claim=preferred_username
    - --kube-apiserver-arg=oidc-username-prefix=oidc:
```

> Rancher の override キー名は版により差があるため、`rdctl` / Preferences の「追加の k3s 引数」欄も参照する。要点は
> apiserver へ上記 4 引数を渡すこと。

いずれの経路でも、これにより Keycloak の `preferred_username=developer` は k8s ユーザー `oidc:developer` にマップされ、
同梱の ClusterRoleBinding（`headlamp-developer-cluster-admin` → `cluster-admin`）でリソースの閲覧・操作ができる。
`developer` は dev スーパーユーザー（IADR-0066）で、ロール別の権限分離検証には使わない（`poc-*` の役割）。

> **実ブラウザでの OIDC 実ログイン・リソース閲覧疎通は稼働 k3d/k3s 依存＝live**（#271 の live 受け入れ・#328）。
> 手順: (1) 上記のとおり OIDC フラグ付きでクラスタを (再)作成 → (2) `HEADLAMP=1 bash scripts/k8s-local-up.sh` →
> (3) 手順A（hosts＋`port-forward svc/keycloak 8080:8080`）＋`port-forward svc/headlamp 4466:80` → (4) ブラウザで
> `http://localhost:4466` を開き `developer` / `developer` でログイン → Pod/Deployment/Service/ログが閲覧できること。
> fail-safe: Headlamp の ServiceAccount には広域権限を bind していないため、OIDC ログイン無しではクラスタを可視化できない。

## 既知の制約

- **観測 UI は非同梱**: otel-collector は dev では `debug` エクスポータのみ（Prometheus/Tempo/Loki/Grafana は
  立てない）。UI が要るなら compose（`deploy/docker-compose.yml`）を併用する。
- **永続化は opt-in**: 既定の infra は emptyDir（Pod 再起動で再 init。dev 用途の割り切り）。`PERSIST=1` で
  Keycloak/Postgres を PVC 永続化できる（上記「永続化」節・IADR-0082）。
- **Istio/mTLS/NetworkPolicy/HPA/エッジ Gateway は無効**（values-local。`edge.enabled=false`）。本番像（STRICT mTLS・
  エッジ `/bff/*` ルーティング等）は不変。経路B の `/bff` 到達は BFF の port-forward で代替する（上記手順）。

## 手動でステップ実行する場合

```bash
# 事前に infra secrets と realm ConfigMap を作成（k8s-local-up.sh が自動化する部分）
kubectl create namespace platform-infra
kubectl create secret generic postgres -n platform-infra --from-literal=password=postgres
kubectl create secret generic rabbitmq -n platform-infra --from-literal=password=guest
kubectl create secret generic keycloak-admin -n platform-infra --from-literal=password=admin
kubectl create configmap keycloak-realms -n platform-infra \
  --from-file=microservices-platform-realm.json=deploy/keycloak/microservices-platform-realm.json

kubectl apply -k deploy/local/infra                                  # infra
helm upgrade --install msp deploy/helm/microservices-platform \
  -n microservices-platform --create-namespace -f deploy/local/values-local.yaml
kubectl apply -f deploy/local/aliases/microservices-platform-externalnames.yaml
```
