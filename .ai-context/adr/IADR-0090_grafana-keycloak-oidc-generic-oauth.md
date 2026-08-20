---
title: IADR-0090 経路B Grafana を Keycloak generic OAuth で認証し匿名 Admin を廃す（fail-safe=local admin フォールバック）
type: impl-adr
status: Accepted
related_ids:
  - ADR-0006
  - IADR-0066
  - IADR-0077
  - IADR-0080
  - IADR-0087
author: claude
created: 2026-07-20
updated: 2026-07-20
plan_refs:
  - planning:projects/microservices-platform/07_adr/ (ADR-0006 CI/CD・運用基盤)
---

# IADR-0090: 経路B Grafana の Keycloak OIDC(generic OAuth) 連携と匿名 Admin 廃止

- 状態: Accepted
- 日付: 2026-07-20
- 決定者: claude（実装）

## 起点・関連

- 関連 ADR: ADR-0006（運用基盤）。経路B 可観測性 opt-in オーバーレイは [IADR-0077](./IADR-0077_local-observability-vault-gitops-overlays.md)、経路B k8s dev 統括は
  [IADR-0066](./IADR-0066_local-k8s-dev-environment.md)、opt-in ゲート横断 smoke test は [IADR-0087](./IADR-0087_k8s-local-up-optin-smoke-test.md)。dev ツールの Keycloak OIDC 連携の先例は
  [IADR-0080](./IADR-0080_headlamp-k8s-management-ui.md)（Headlamp・confidential client・secret を k8s Secret 供給）。
- 仕様書: `docs/specs/20260720_issue-353_grafana-keycloak-oidc.md`。
- Issue: #353（親・enhancement）「経路B ローカルツールの Keycloak SSO 一括連携」の子タスク 1（Grafana）。

## コンテキストと課題

経路B の Grafana（`deploy/local/observability/grafana.yaml`）は匿名 Admin（`GF_AUTH_ANONYMOUS_ENABLED=true`＋
`ORG_ROLE=Admin`）で立ち、認証なしで誰でも Admin 相当を操作できる。Keycloak は経路B の単一 IdP として稼働し、
既に `spa-web`/`bff`/`wiki-js`/`headlamp` の OIDC クライアントを持つ。Grafana も SSO に載せ、匿名フルアクセスを
廃したい。制約は「fail-safe（認証未設定/失敗時に匿名フルアクセスへ倒れない）」「秘密は非平文」「opt-in・後方互換」。

## 決定

### 1. generic OAuth（OIDC）で Keycloak に連携する

Grafana の `GF_AUTH_GENERIC_OAUTH_*` env で `microservices-platform` realm に連携する。issuer は
`http://keycloak:8080/realms/microservices-platform`（`headlamp` と同一・#284 手順A で browser も `keycloak:8080`
を解決し iss を一致させる）。auth/token/userinfo URL を同 issuer 配下に揃える。realm に confidential client
`grafana`（redirectUris=`http://localhost:3000/login/generic_oauth`）を**追加**する。

### 2. fail-safe：匿名 Admin を無効化し、フォールバックは local admin

`GF_AUTH_ANONYMOUS_ENABLED=false` にする。ロックアウト回避のフォールバックは Grafana 組み込みの **local admin**
（dev 既定 `admin`/`admin`）とする。匿名フルアクセスには**倒さない**。さらに client secret を注入する
`grafana-oidc` Secret 参照を **`optional: true`** とし、Secret 不在（＝OIDC 未設定）でも Pod は起動し、OIDC 失敗時は
local admin ログインに落ちる。すなわち「OIDC 正常＝SSO」「OIDC 未設定/失敗＝local admin」「匿名フルアクセス＝どの
経路でも発生しない」。

### 3. role マッピングは realm ロール由来・未知は Viewer（最小権限）

`role_attribute_path`（JMESPath）で `platform-admin`→`Admin`、`platform-operator`→`Editor`、それ以外→`Viewer`。
`role_attribute_strict=false`。フォールバックを `Viewer` に固定し、ロール無し/未知ユーザーが Admin に昇格しない
（deny-by-default 的最小権限）。

### 4. レルムロールはクライアント内 protocolMapper で id_token/userinfo に載せる

共有 `roles` clientScope はレルムロールを **access token 限定**（`id.token.claim=false`/`userinfo.token.claim=false`）
で発行する（backend の `KeycloakRolesClaimsTransformation` が access token を読むため。共有スコープは不変に保つ）。
一方 Grafana の role_attribute 評価は id_token/userinfo を対象とするため、access token 限定のクレームは届かない。
そこで **`grafana` クライアント内の protocolMapper** でレルムロールを平坦な `roles` クレームとして id_token/userinfo
（＋access token）に発行する。クライアント固有のため他クライアント（bff/spa-web）へ影響しない。

### 5. 秘密は非平文・k8s Secret 供給（Headlamp 先例に一致）

realm export の client secret はプレースホルダ `grafana-dev-secret-change-me`。Grafana へは k8s Secret
`grafana-oidc`（`k8s-local-up.sh` が `OBSERVABILITY=1` ブロックで dev 既定 or `GRAFANA_OIDC_CLIENT_SECRET` env で
作成）から注入する。[IADR-0080](./IADR-0080_headlamp-k8s-management-ui.md) の `headlamp-oidc` と同型。

## 影響・トレードオフ

- 匿名 Admin の廃止で、経路B の Grafana はログイン（OIDC or local admin）が必要になる。dev の摩擦は増えるが、
  #353 の SSO 一括連携方針（匿名フルアクセスを廃す）と fail-safe 要件に合致する。break-glass は local admin。
- 変更は opt-in オーバーレイ（`OBSERVABILITY=1`）と realm への追加に限定。既定オフ時のスクリプト挙動はバイト等価
  （回帰は `k8s-local-up.test.js` で固定）。本番 Helm・compose（経路A）・他 realm クライアントは不変。
- 実ブラウザ SSO ログインの疎通は稼働 k3d/Keycloak と #284 手順A（hosts+port-forward）に依存し、live で検証する。

## 代替案

- **匿名を残しつつ OIDC を追加**: fail-safe 要件（匿名フルアクセスへ倒れない）に反するため却下。
- **共有 `roles` clientScope を id_token/userinfo にも出す**: backend 前提（access token 読み取り）と結合し
  全クライアントへ波及するため却下。クライアント固有 protocolMapper に閉じる。
- **client secret を manifest に平文/CLI 生成**: 秘密の非平文原則に反する。k8s Secret + env 上書きに一元化。
