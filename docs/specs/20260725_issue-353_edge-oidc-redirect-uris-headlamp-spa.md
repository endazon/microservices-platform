---
title: 経路B エッジ集約の欠落修正 — headlamp / spa-web の集約後 redirect URI を realm に追加（Issue #353・SSO フォローアップ）
type: spec
status: done
related_ids:
  - IADR-0033
  - IADR-0080
  - IADR-0091
author: claude
created: 2026-07-25
updated: 2026-07-25
related_specs:
  - "../adr/IADR-0091_local-edge-aggregation-traefik.md"
  - "../adr/IADR-0080_headlamp-k8s-management-ui.md"
  - "../adr/IADR-0033_frontend-spa-foundation.md"
  - "../../deploy/keycloak/microservices-platform-realm.json"
  - "../../deploy/local/edge/README.md"
  - "../../src/platform/frontend/src/foundation/auth/authConfig.ts"
---

# 仕様書: 経路B エッジ集約後 URL の redirect URI 欠落修正（headlamp / spa-web）

## 起点となる計画書（トレーサビリティ）

- 決定: [[IADR-0091]]（ローカルエッジ集約・Traefik。`*.localhost:50000` / フロント 80/443）のフォローアップ。
  個別 SSO は [[IADR-0080]]（Headlamp OIDC）・[[IADR-0033]]（SPA foundation・OIDC public client + PKCE）。
- Issue: #353（エッジ/SSO 集約の親・enhancement）。

## 背景と問題

エッジ集約（IADR-0091）適用後、管理ツール UI は `*.localhost:50000`、platform フロントは 80/443（`http://localhost/`）で
到達する。grafana / argocd / minio / vault の各 client は集約後 URL を realm に登録済みだが、次の 2 client で
**集約後 redirect URI が未登録**のため、エッジ経由のブラウザ OIDC が `invalid redirect_uri` で完了しない。

- **`headlamp`**: `redirectUris` が port-forward 用 `http://localhost:4466/*` のみ。集約後 URL
  `http://headlamp.localhost:50000/*` が未登録。
- **`spa-web`**: `redirectUris` が `http://localhost:3100/*` / `http://localhost:8081/*` のみ。フロントのエッジ origin
  `http://localhost`（80番）が未登録。SPA は `redirect_uri = <origin>/callback`（[authConfig.ts](../../src/platform/frontend/src/foundation/auth/authConfig.ts)、
  callback パスは `/callback`）、ログアウトは `post_logout_redirect_uri = <origin>` を送るため、
  `redirectUris` と `attributes.post.logout.redirect.uris` の双方にエッジ URL が要る。

## 受け入れ基準

1. `headlamp` の `redirectUris`/`webOrigins` に `http://headlamp.localhost:50000/*` / `http://headlamp.localhost:50000` を追加。
2. `spa-web` の `redirectUris`/`webOrigins` に `http://localhost/*` / `http://localhost` を追加し、
   `attributes.post.logout.redirect.uris` にも `http://localhost/*` を追加（`##` 区切り）。
3. 既存の port-forward 用 URL は残す（後方互換）。他 client・他フィールドは無改変。
4. realm JSON が妥当で、`scripts/check-realm-constraints.js`（varchar(255) ガード・Issue #18）が green。
5. 本番 chart・アプリコードは無改変。gitleaks green。

## 実装

- 変更ファイル: [`deploy/keycloak/microservices-platform-realm.json`](../../deploy/keycloak/microservices-platform-realm.json)（redirect URI 追加のみ）。
- ドキュメント: [`deploy/local/edge/README.md`](../../deploy/local/edge/README.md) の OIDC 節に、platform フロント（`http://localhost/`）と
  headlamp の集約後 redirect 登録済みを追記。
- 新規 IADR は不要（新たな設計判断は無く、IADR-0091/0080/0033 の適用漏れ修正のため `fix(IADR-0091)` で参照）。

## 検証

- `node scripts/check-realm-constraints.js deploy/keycloak/microservices-platform-realm.json` → OK。
- `node -e JSON.parse(...)` で JSON 妥当性を確認。
- realm の URL セットを固定する回帰テストは存在しない（`scripts/scripts.test.js` は制約長ロジックのみ検査）。
