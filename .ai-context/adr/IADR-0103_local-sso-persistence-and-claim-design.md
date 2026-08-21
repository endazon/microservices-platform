---
title: IADR-0103 経路B の SSO を再構築後も自動復旧させる（admin ユーザー恒久化・ツール別 claim 設計・ESO 後の rollout・argocd DNS エイリアス）
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0004
  - ADR-0006
  - IADR-0080
  - IADR-0084
  - IADR-0090
  - IADR-0091
  - IADR-0092
  - IADR-0093
  - IADR-0094
  - IADR-0095
  - IADR-0096
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0004_authz-abac.md (認可＝ABAC。認証は Keycloak に一元化)
  - planning:projects/microservices-platform/07_adr/ADR-0007_cicd-gitops-argocd.md (GitOps=ArgoCD)
  - planning:projects/microservices-platform/02_requirements/ (NFR 運用性＝再構築の再現性)
author: claude
created: 2026-07-25
updated: 2026-07-25
---

# IADR-0103: 経路B の SSO を再構築後も自動復旧させる

- 状態: Accepted
- 日付: 2026-07-25
- 決定者: claude（実装）

## 背景

#353 系（IADR-0090/0092/0093/0094/0095）で Grafana/ArgoCD/MinIO/Vault/Wiki.js の Keycloak SSO を、#310 系
（IADR-0096〜0099）で Vault＋ESO の secret 供給を入れた。しかし実際に経路Bを立ち上げると **6 ツールのうち
5 つで管理者ログインが成立せず**、その修復に必要な設定が**すべて揮発（realm 再インポート・Pod 再起動で消える）
live 操作**だった。原因は個別の設定漏れではなく、次の 4 つの構造的欠落である。

1. **realm に管理者ユーザーが居ない**: `developer`/`poc-*` のみ。各ツールが管理者判定に使う claim を持つ
   ユーザーが realm に恒久定義されていなかった。
2. **MinIO の `policy` claim が多値**: realm ロールをそのまま流していたため
   `["consoleAdmin","wiki-editor","offline_access",...]` となり、MinIO が存在しないポリシー名を解決できず
   **callback で 500**。
3. **ESO が Secret を作るのは Pod 起動より後**: env の `secretKeyRef` は **Pod 起動時に一度だけ解決され、
   その後の Secret 更新は既存 Pod へ反映されない**。そのため対象 Pod は「空」（`optional: true` 参照＝MinIO /
   Grafana）または「旧値」（既存 Secret を ESO が上書き＝LlmGateway）の env を保持し続ける。実害として
   MinIO=`unauthorized_client`、Grafana=client_secret 空、LlmGateway=`API key is invalid` が同時発生した。
4. **`argocd` namespace に `keycloak` エイリアスが無い**: DNS がクラスタ内で解決できずノードのリゾルバへ
   フォールスルーし、手順A のために hosts へ入れた `127.0.0.1 keycloak` を拾って **argocd-server が自分自身の
   :8080 へ discovery を投げ 404**（`failed to query provider ...: 404`）。

## 決定

### 1. `admin` ユーザーを realm に恒久定義する

`deploy/keycloak/microservices-platform-realm.json` に `admin`（dev パスワード `admin`）を追加し、
`platform-admin` / `platform-operator` / `wiki-editor` / `Administrators` と ABAC グループ
（`/clearance/restricted`・`/department/engineering`）を持たせる。これ 1 ユーザーで Grafana=Admin /
ArgoCD=role:admin / Vault=admin policy / Wiki.js=Administrators / MinIO=consoleAdmin に解決される。

`developer` は**据え置き**（SPA/BFF の疎通用スーパーユーザー・IADR-0066）。ロール別の権限分離検証は
`poc-*` の役割であり、本 ADR は変えない。

### 2. ツール別 claim は「そのツールが解釈できる名前空間」で供給する

各ツールは claim を**自分の権限モデルの識別子として直接解釈する**ため、realm ロール名をそのまま流すと
ツール側の語彙と一致しない。ツールごとに次の形を採る。

| ツール | claim | 供給方式 | 理由 |
| --- | --- | --- | --- |
| Grafana | `roles` | realm ロール（多値可） | JMESPath で `contains(roles[*],'platform-admin')` を評価するだけなので多値で問題ない |
| ArgoCD | `groups` | realm ロール（多値可） | `policy.csv` の `g, <group>, role:*` が一致行を拾うため多値で問題ない |
| Vault | `groups` | realm ロール（多値可） | external group 名と一致した分だけ policy が付く（多値で問題ない） |
| **MinIO** | `policy` | **`minio` client ロール（`consoleAdmin`）** | **MinIO はポリシー名として解決するため多値だと失敗する**。client ロールに閉じることで claim が単一値になる |
| **Wiki.js** | `groups` | realm ロール ＋ **realm ロール `Administrators`** | Wiki.js は自前グループ管理で、**グループ名の文字列一致**が唯一の接点。ロール名を Wiki.js のグループ名に合わせる |

- MinIO は `oidc-usermodel-client-role-mapper`（`usermodel.clientRoleMapping.clientId=minio`）に差し替え、
  旧 `minio-realm-roles`（realm ロール多値）は**削除**する。副作用として、client ロール未付与のユーザー
  （`developer` 等）は `policy` claim が付かず MinIO にログインできない＝**deny-by-default** になる。
- **単一値は運用制約に依存する**（claude-review 🟡 反映）: mapper 自体は `"multivalued": "true"` で複数付与時に
  多値配列を返すため、`policy` claim が単一値である保証は「**1 ユーザーに `minio` client ロールを 1 つだけ付与する**」
  という運用前提に依存する。複数付与すると `policy` claim が多値化し、対策したはずの callback 500 が再発する。
  この逸脱は `scripts/k8s-local-up.test.js` の `admin.clientRoles.minio` 要素数 === 1 アサーションで機械検知する。
- Wiki.js/Headlamp の `wiki-js`/`headlamp` client には `groups` claim mapper を追加する（他ツールと同型）。
  Headlamp 分は現行 k8s では inert だが、HTTPS 化（#388）でそのまま使えるため同時に入れる。

**トレードオフ**: ツール名前空間に合わせたロール（`consoleAdmin`・`Administrators`）が realm に増え、ロール名前
空間が「権限の抽象」ではなく「ツールの実装語彙」に寄る。dev 環境の SSO 疎通を最小手数で成立させることを優先し、
ツール側設定（MinIO のポリシー作成・Wiki.js のグループ追加）を触らない方を選んだ。

### 3. ESO 供給後に SecretSynced を待ってから対象 Deployment を rollout する

`ESO=1` ブロックの末尾で、**ESO 管理 Secret を env(`secretKeyRef`) で参照する Deployment を網羅的に**
`rollout restart` する。対象と参照する Secret は次の通り。

| ns | Deployment | 参照する ESO 管理 Secret |
| --- | --- | --- |
| `microservices-platform` | `minio` | `minio-credentials`（root）/ `minio-oidc`（client secret） |
| `microservices-platform` | `llmgateway-service` | `llm-provider-credentials`（`Llm__ApiKey`） |
| `microservices-platform` | `wiki-service` | `wikijs-sync`（`WikiJs__ApiKey`） |
| `microservices-platform` | `wiki-js` | `wikijs-db`（`DB_PASS`） |
| `platform-infra` | `grafana`（`OBSERVABILITY=1` 時） | `grafana-oidc` |
| `platform-infra` | `headlamp`（`HEADLAMP=1` 時） | `headlamp-oidc` |

**対象外**: `postgres` / `rabbitmq` / `keycloak-admin` は `creationPolicy: Merge` で seed（step 3）と**同一値**を
マージするだけなので env は変化せず、再起動は DB/broker を無用に落とすだけである（[IADR-0099](./IADR-0099_vault-eso-secret-supply-pr4.md)）。
`vault-oidc` は env 参照が無く、`bootstrap.sh` が CLI で読むため rollout 不要。

**rollout の前に `SecretSynced` を待つ**: ExternalSecret の `condition=Ready`（ESO の
`status=True` / `reason=SecretSynced`）を `kubectl wait` で待機してから restart する。待たずに restart すると
**新 Pod もまだ供給前の Secret を掴んで同じ状態で固定され、rollout が無駄打ちになる**（ESO の初回同期は
`kubectl apply` 直後には完了していない）。待機時間は `ESO_SYNC_TIMEOUT`（既定 `90s`）で上書きできる。

待機・rollout とも **best-effort**（未デプロイ・未有効ゲート・同期遅延で `up` を止めない）。同期しなかった
場合は `warn:` を出して rollout へ進む（`up` 全体を落とすより、残りの手順を進めて runbook で復旧する方が
dev の再構築では速い）。

secret を Pod 起動前に作る順序へ組み替える案は、Vault 自体が後段で起動する（chicken-and-egg）ため採らない。
「供給後に env を作り直す」方が構造が単純で、ESO の refresh でも同じ問題が起きうるため rollout が本質的な解になる。

### 4. `argocd` namespace に `keycloak` ExternalName エイリアスを張る

`deploy/local/aliases/argocd-externalnames.yaml` を追加し、`ARGOCD=1` ブロックが適用する。
issuer は in-cluster 正準名（`http://keycloak:8080/...`＝token の `iss` と一致）のままにし、
**名前解決だけを正す**。metadata/issuer 分離（IADR-0086）は不要である（issuer は元から正しく、
壊れていたのは DNS だけ）。

### 5. Vault の `auth/oidc` を UI のログイン候補に出す

`oidc/bootstrap.sh` に `vault auth tune -listing-visibility=unauth` を追加する。既定 hidden では未認証の
`sys/internal/ui/mounts` が `auth: {}` を返し、**UI に OIDC が現れない**（Token 入力しか見えず「ログイン不能」に見える）。

## 影響・非対象

- **dev 専用**（`deploy/local` ＋ dev realm）。本番 chart（`deploy/helm`）・ArgoCD 描画・compose は無改変。
- `ESO`/`ARGOCD` ゲート未設定時の挙動は不変（rollout もエイリアス適用も opt-in ブロック内）。
- **実弾・取引には無関係**（`Broker__Provider=paper`・opend 不在は本 ADR の変更対象外で不変）。
- Headlamp の OIDC 化は**本 ADR の対象外**。現行 k8s では issuer の https 強制で不可能（[IADR-0084](./IADR-0084_headlamp-oidc-apiserver-flags.md) の追記参照）。
  正規手順は SA トークン方式で、HTTPS 化と同時の対応を **#388** で追跡する。
- realm 再インポート/Pod 再起動時の復旧手順は `docs/operations/local-sso-recovery-runbook.md` に集約する。

## 却下した代替案

- **MinIO 側に `platform-admin` という名のポリシーを作る**: `mc admin policy` の実行（＝MinIO の状態変更）が
  復旧手順に増える。realm 側の client ロールだけで閉じる方が再構築時に自動化しやすい。
- **`policy` claim を hardcoded claim（常に `consoleAdmin`）にする**: client の全ユーザーが管理者になり
  deny-by-default を壊す。
- **Wiki.js 側に `platform-admin` グループを作る**: Wiki.js の DB 状態が増え、権限定義が realm と Wiki.js に
  二重化する。realm ロール名を合わせる方が単一情報源に近い。
- **ESO の secret 作成を `up` の先頭へ移す**: Vault が後段起動のため不可能（chicken-and-egg）。
- **ArgoCD の issuer をエッジ host へ変更（metadata/issuer 分離）**: token の `iss` と不一致になり、
  分離設定（IADR-0086）が追加で必要。原因は DNS なのでエイリアスで足りる。
