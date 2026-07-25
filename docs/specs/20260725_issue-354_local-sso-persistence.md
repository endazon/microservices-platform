---
title: "経路B SSO の恒久化（admin ユーザー・ツール別 claim・ESO 後 rollout・argocd DNS エイリアス）（Issue #354）"
type: spec
status: done
related_ids:
  - IADR-0103
  - IADR-0084
  - IADR-0091
  - IADR-0093
  - IADR-0094
  - IADR-0095
  - IADR-0096
author: claude
created: 2026-07-25
updated: 2026-07-25
related_specs:
  - "../adr/IADR-0103_local-sso-persistence-and-claim-design.md"
  - "../operations/local-sso-recovery-runbook.md"
  - "../../deploy/keycloak/microservices-platform-realm.json"
  - "../../scripts/k8s-local-up.sh"
  - "../../scripts/k8s-local-up.test.js"
---

# 仕様書: 経路B SSO の恒久化（Issue #354）

## 起点

`#354`（ローカルk8s立ち上げ振り返り）。#353 系で入れた SSO と #310 系で入れた ESO 供給が、実際の立ち上げでは
**6 ツールのうち 5 つで管理者ログインできず**、修復に使った設定がすべて**揮発する live 操作**だった。
設計判断を伴うため **IADR-0103** を採番する。

## 対象とした 4 つの構造的欠落（実測で確定）

| # | 症状 | 原因 |
| --- | --- | --- |
| 1 | 各ツールで管理者ログインできない | realm に**管理者ユーザーが不在**（`developer`/`poc-*` のみ） |
| 2 | MinIO の callback が **500** | `policy` claim が realm ロール多値 → 存在しないポリシー名を解決できない |
| 3 | MinIO `unauthorized_client` / Grafana client_secret 空 / LlmGateway `API key is invalid` | **ESO が Secret を作るのは Pod 起動より後**。env の `secretKeyRef` は起動時に一度だけ解決されるため、env が「空」（`optional` 参照）または「旧値」（ESO が既存 Secret を上書き）のまま固定 |
| 4 | ArgoCD `failed to query provider ...: 404` | `argocd` ns に `keycloak` エイリアスが無く、DNS がノードへフォールスルーして手順A の hosts `127.0.0.1 keycloak` を拾い、**自分自身へ discovery** |
| 5 | Vault UI に OIDC が出ない | `auth/oidc` の `listing_visibility` が既定 hidden |

## 変更内容

### コード / 自動化

| ファイル | 変更 |
| --- | --- |
| `deploy/keycloak/microservices-platform-realm.json` | `admin` ユーザー追加（roles: `platform-admin`/`platform-operator`/`wiki-editor`/`Administrators`、clientRoles: `minio:consoleAdmin`、groups: `/clearance/restricted`・`/department/engineering`、dev パスワード）／realm ロール `Administrators` 追加／**client ロール `minio:consoleAdmin`** 追加／`minio` の mapper を `minio-realm-roles`（realm ロール多値）→ **`minio-client-roles`**（`oidc-usermodel-client-role-mapper`）へ差し替え／`wiki-js`・`headlamp` に `groups` claim mapper 追加 |
| `deploy/local/aliases/argocd-externalnames.yaml`（新規） | `argocd` ns の `keycloak` ExternalName |
| `scripts/k8s-local-up.sh` | `ARGOCD=1`: 上記エイリアスを apply（＋既存の `argocd-server` rollout で反映）／`ESO=1` 末尾: 対象 ExternalSecret の **`SecretSynced`（`condition=Ready`）を待ってから**、ESO 管理 Secret を env 参照する `minio`・`llmgateway-service`・`wiki-service`・`wiki-js`（＋ゲート有効時 `grafana`・`headlamp`）を **best-effort rollout**。`postgres`/`rabbitmq`/`keycloak-admin` は Merge で同一値のため**対象外** |
| `deploy/local/vault/oidc/bootstrap.sh` | `vault auth tune -listing-visibility=unauth -description="Keycloak SSO (OIDC)" oidc/` を追加 |
| `scripts/k8s-local-up.test.js` | 回帰 6 件（下記） |

### ドキュメント

| ファイル | 変更 |
| --- | --- |
| `docs/adr/IADR-0103_*.md`（新規） | 上記 5 点の決定と却下案 |
| `docs/adr/IADR-0084_*.md` | **⚠️ 追記**: k8s 1.30+ は OIDC issuer に **https 必須**（`jwt[0].issuer.url`）で本 ADR の手順は**適用するとクラスタ停止**。正規手順は SA トークン方式。`config.yaml.d` の末尾コロンはクォート必須。到達性検証は k3s の netns から行う |
| `docs/operations/local-sso-recovery-runbook.md`（新規） | 揮発マトリクスと復旧手順（通常は STEP 0 のみで足りる） |
| `deploy/local/vault/oidc/README.md` | **vault Pod 内 CLI での実行手順**（ホストに `vault` CLI が無い環境向け）＋ `listing_visibility` の説明 |
| `deploy/local/wiki-oidc/README.md` | **DB seed 手順**（`authentication` 行・`settings.host`・必要 prop・`groups` mapper と `Administrators` ロールの前提） |
| `deploy/local/edge/README.md` | **admin entrypoint(50000) は平文 http のみ**（`https://` は 404） |
| `docs/adr/README.md` | IADR-0103 の索引行 |

## 非対象

- **Headlamp の OIDC 化**: 現行 k8s では issuer の https 強制により不可能。SA トークン方式を維持し、
  HTTPS 化と同時の対応を **#388** で追跡（IADR-0084 に追記）。
- 本番 chart（`deploy/helm`）・ArgoCD 描画・compose・realm の `developer`/`poc-*`・**取引/実弾**は無改変。

## 受け入れ基準と検証

- [x] `realm.json` が JSON として妥当・`node scripts/check-realm-constraints.js` が OK（255 文字制約）
- [x] `admin` が `platform-admin`/`platform-operator`/`wiki-editor`/`Administrators` ＋ `minio:consoleAdmin` を持つ
- [x] `minio` の mapper が `oidc-usermodel-client-role-mapper`（claim `policy`）で、旧 realm ロール mapper が無い
- [x] `wiki-js`・`headlamp` に claim `groups` の mapper がある
- [x] `ARGOCD=1` で `argocd-externalnames.yaml` が apply される（回帰テスト）
- [x] `ESO=1` で `minio`/`llmgateway-service`/`wiki-service`/`wiki-js` の rollout が発行される。**既定（ESO 未設定）では発行されない**（回帰テスト）
- [x] `ESO=1` で `postgres`/`rabbitmq`/`keycloak` は rollout されない（Merge 供給＝同一値。DB/broker の無用な再起動を防ぐ・回帰テスト）
- [x] `ESO=1` で rollout の**前に** ExternalSecret の `SecretSynced`（`condition=Ready`）待ちが発行される（順序を回帰テストで固定）
- [x] `oidc/bootstrap.sh` に `listing-visibility=unauth` がある（回帰テスト）
- [x] `node scripts/k8s-local-up.test.js` / `scripts/scripts.test.js` / `check-doc-links.js` が green
- [x] 本番 chart・compose・realm の既存ユーザー定義は無改変（`git diff` で確認）

## 実測で確認した live 挙動（本 PR の根拠）

- ArgoCD: エイリアス適用＋`argocd-server` rollout 後、`/auth/login` が **404→303**（Keycloak の authorize へ）
- MinIO: client ロール化で `policy` claim が `["consoleAdmin"]` の**単一値**（`developer` は `null`＝deny-by-default）
- Vault: `listing_visibility=unauth` で未認証 `sys/internal/ui/mounts` が `{"oidc/":...}` を返す
- Wiki.js: `Authentication Strategy Keycloak: [ OK ]` ＋ `/login/{key}` が 302
- ESO rollout: `minio` の `MINIO_IDENTITY_OPENID_CLIENT_SECRET` が **0 → 26 バイト**、Grafana が **0 → 28 バイト**、
  LlmGateway が旧鍵→最新鍵（`/complete` が `sent:false` → **`sent:true`**）
