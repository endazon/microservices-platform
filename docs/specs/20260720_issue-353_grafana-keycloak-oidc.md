---
title: 経路B Grafana を Keycloak OIDC(generic OAuth) 連携し匿名 Admin を廃する（Issue #353・SSO 一括連携 その1）
type: spec
status: done
related_ids:
  - ADR-0006
  - IADR-0066
  - IADR-0077
  - IADR-0087
  - IADR-0090
author: claude
created: 2026-07-20
updated: 2026-07-20
related_specs:
  - "../adr/IADR-0090_grafana-keycloak-oidc-generic-oauth.md"
  - "../adr/IADR-0077_local-observability-vault-gitops-overlays.md"
  - "../adr/IADR-0066_local-k8s-dev-environment.md"
  - "../adr/IADR-0087_k8s-local-up-optin-smoke-test.md"
  - "../../deploy/local/observability/grafana.yaml"
  - "../../deploy/local/observability/README.md"
  - "../../deploy/keycloak/microservices-platform-realm.json"
  - "../../scripts/k8s-local-up.sh"
  - "../../scripts/k8s-local-up.test.js"
---

# 仕様書: 経路B Grafana の Keycloak OIDC 連携（Issue #353 子タスク 1/6）

## 起点となる計画書（トレーサビリティ）

- ADR: ADR-0006（CI/CD・運用基盤）。経路B の可観測性 opt-in オーバーレイの統括は [[IADR-0077]]、
  経路B k8s dev 環境の統括は [[IADR-0066]]、opt-in ゲート横断 smoke test は [[IADR-0087]]。
- 決定: 本作業の設計判断は [[IADR-0090]]（Grafana generic OAuth・匿名廃止・fail-safe）。
- Issue: #353（親・enhancement）「経路B ローカルツールの Keycloak SSO 一括連携」の**子タスク 1（Grafana）**。

## 背景と問題

経路B（`OBSERVABILITY=1 bash scripts/k8s-local-up.sh`）の Grafana（`deploy/local/observability/grafana.yaml`）は
現状 **匿名 Admin** で立つ:

```yaml
env:
  - { name: GF_AUTH_ANONYMOUS_ENABLED, value: "true" }
  - { name: GF_AUTH_ANONYMOUS_ORG_ROLE, value: "Admin" }
```

認証なしで誰でも Admin として全ダッシュボード/データソースを操作できる。realm には既に `spa-web`/`bff`/
`wiki-js`/`headlamp` の OIDC クライアントがあり、Keycloak は経路B の単一 IdP として稼働している。
本システムの他ツール（Headlamp #271/#328）と同様、Grafana も Keycloak SSO に載せ、匿名フルアクセスを廃する。

## 受け入れ基準

1. Grafana が Keycloak(`microservices-platform` realm) の OIDC(generic OAuth) でログインできる配線がある
   （realm クライアント `grafana` ＋ Grafana の `GF_AUTH_GENERIC_OAUTH_*` env）。
2. **fail-safe（安全側の既定）**: 認証未設定/失敗時に**匿名フルアクセスへ倒れない**。匿名 Admin は無効化し、
   ロックアウト回避のフォールバックは Grafana 組み込み **local admin**（`admin`/`admin` dev 既定）とする。
3. **role マッピング**: `realm_access` のレルムロールで Grafana の org ロールを決める。`platform-admin`→`Admin`、
   `platform-operator`→`Editor`、それ以外→`Viewer`（最小権限・未知ユーザーは Viewer に倒す）。
4. **秘密の非平文**: client secret は realm export ではプレースホルダ（`grafana-dev-secret-change-me`）、
   Grafana へは k8s Secret `grafana-oidc`（`k8s-local-up.sh` が dev 既定 or env で作成・平文コミットなし）経由。
5. **後方互換・opt-in**: 変更は経路B の opt-in オーバーレイ（`deploy/local/observability`）と realm への
   **追加のみ**に閉じる。本番 Helm チャート（`deploy/helm`）・compose（経路A）・他 realm クライアントは不変。
6. **issuer 一致**: Authority/issuer は `http://keycloak:8080/realms/microservices-platform`（`headlamp` 先例・
   #284 手順A：browser も `keycloak:8080` を解決）。auth/token/userinfo URL を同 issuer に揃える。
7. CI 緑: `check-realm-constraints`（description ≤255）・`k8s-local-up.test.js`（grafana-oidc secret 分岐）・
   `doc-links`・`check-image-mapping`(#275・image 不変) を通す。

## 対応方針（変更範囲）

`deploy/local/observability/grafana.yaml`（env）・`deploy/keycloak/microservices-platform-realm.json`
（`grafana` クライアント追加）・`scripts/k8s-local-up.sh`（OBSERVABILITY ブロックで `grafana-oidc` secret 作成）・
`scripts/k8s-local-up.test.js`（回帰ガード）・README（手順）・`docs/adr`（IADR-0090＋索引）に閉じる。

1. **realm クライアント `grafana`（confidential・追加のみ）**: `standardFlowEnabled`、redirectUris
   `http://localhost:3000/login/generic_oauth`、webOrigins `http://localhost:3000`。secret はプレースホルダ。
   レルムロールを id_token/userinfo に載せる**クライアント内 protocolMapper**（`roles` 平坦クレーム）を持たせる
   （共有 `roles` clientScope は access token 限定＝Grafana の role_attribute 評価に届かないため。共有スコープは不変）。
2. **Grafana env（`grafana.yaml`）**: 匿名を無効化し generic OAuth を有効化。client secret は `grafana-oidc`
   secret を **`optional: true`** で参照（secret 不在でも Pod は起動＝fail-safe に local admin へ）。
   `role_attribute_path` は `roles` クレームで Admin/Editor/Viewer を決定、`role_attribute_strict=false`。
3. **`k8s-local-up.sh`**: `OBSERVABILITY=1` ブロックで overlay 適用前に
   `apply_secret platform-infra grafana-oidc client-secret=${GRAFANA_OIDC_CLIENT_SECRET:-grafana-dev-secret-change-me}`
   を作成（headlamp-oidc と同型）。既定オフ時は一切実行されない（バイト等価）。
4. **README**: `deploy/local/observability/README.md` に Grafana ログイン手順（port-forward・OIDC・local admin
   フォールバック・secret 上書き env）を追記。
5. **回帰ガード（TDD）**: `k8s-local-up.test.js` に (a) 既定オフで `grafana-oidc` が現れない、(b) `OBSERVABILITY=1`
   で `grafana-oidc` secret が作られる、の 2 点を追加。

## 非対象（スコープ外）

- compose（経路A）の Grafana OIDC 化 → follow-up（#353 は経路B を対象）。
- 本番 Helm の Grafana（そもそも chart に無い）・他ツール（ArgoCD/MinIO/Vault/Wiki.js）は #353 の別子タスク。
- Grafana local admin パスワードの堅牢化（dev は `admin`/`admin` 既定を break-glass として据え置き）。
- 実ブラウザ SSO ログインの疎通確認（稼働 k3d/Keycloak 依存・#284 手順A）は live 検証（別途）。

## 検証

- `node scripts/check-realm-constraints.js --self-test && node scripts/check-realm-constraints.js`
- `node scripts/k8s-local-up.test.js`
- `node scripts/check-doc-links.js`
- `node scripts/check-image-mapping.js`（image 不変の確認）
- `python -c "import json; json.load(open('deploy/keycloak/microservices-platform-realm.json'))"`（JSON 妥当性）
