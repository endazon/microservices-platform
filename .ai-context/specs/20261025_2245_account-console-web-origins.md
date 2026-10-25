---
title: 作業仕様書 — エッジ越しのアカウントコンソールの token の交換が 403（Invalid origin）になるのを account-console の webOrigins で直す（#2245）
type: spec
status: in-progress
related_ids: [SC-16, NFR, NFR-18, ADR-0026, ADR-0032, IADR-0524, IADR-0546]
author: claude
created: 2026-10-25
updated: 2026-10-25
issue: "#2245"
---

# 作業仕様書 — account-console の webOrigins（#2245）

> 本仕様書は実装着手前に作成した（着手 2026-10-25）。発端は #2241（E-54）の M-007。オーケストレーターの裁定 2026-10-25（(c) 案: 修正の PR を先に出す）。
> IADR は新しく起こさず、IADR-0524（転送ヘッダを読ませない決定）へ日付つきの追記で残す（その決定の帰結であるため・#2241 の IADR-0634 との採番の衝突を避けるため）。

## 受け入れ基準（#2245 ＋ オーケストレーターの条件 2026-10-25）

- [ ] realm 宣言の `account-console` が `webOrigins: ["+"]` を持つ
- [ ] `reconcile-realm.js` が既存の realm（`webOrigins` が空の `account-console`）へ同じ値を入れる。冪等（当てた後は 0 件）で、ほかの設定に触れない（試験で固定）
- [ ] `check-realm-constraints.js` の検査 10 が `webOrigins` の欠落を止める（変異で赤になる）
- [ ] e2e-live が緑（PR の CI。develop の試験で流れる。M-007 の緑は #2245 のマージ後に #2241 で確かめる）

## 原因（#2241 の診断で確定）

- e2e-live（#2241 a683db8b）で、アカウントコンソールの `POST /realms/platform/protocol/openid-connect/token` が **403**。Keycloak のイベントは出ない。
- Keycloak 26.7.4 の `OAuth2GrantTypeBase.checkClient` → `DefaultCors.checkOrigin()`: `Origin` が `allowedOrigins`（client の `webOrigins`）にも、
  要求の origin（`KeycloakUriInfo.getRequestUri()`＝**KC_HOSTNAME を当てる前の生の要求 URI**）にも一致しなければ `ForbiddenException("Invalid origin")`。
- エッジ（Traefik）が TLS を終端し、`KC_PROXY_HEADERS` は置かない（IADR-0524）ので、Keycloak から見た要求の origin は `http://keycloak.localhost`。
  ブラウザの `Origin` は `https://keycloak.localhost`。`account-console` の `webOrigins` は `[]`（Keycloak 組み込みの作成も web origin を足さない）。
- BFF は機密クライアントでサーバ間で交換するので `Origin` が無く、CORS の検査を通る（これまで通っていた説明と矛盾しない）。

## 判断

1. **`webOrigins: ["+"]`**: `+` は redirect URI（`/realms/platform/account/*`。`rootUrl=${authBaseUrl}` に対して解決）から origin を作る。`${authBaseUrl}` は
   frontend の base（KC_HOSTNAME）で解決されるので `https://keycloak.localhost` を許す。環境の host を宣言に書かない（PoC・本番の host でも同じ宣言で成り立つ）。
2. **`KC_PROXY_HEADERS` は採らない**（IADR-0524 が退けた。メッシュの送り手は `X-Forwarded-Port` を付けず、jwks の URL が壊れる）。
3. **reconcile は既存の仕組みで当たる**: client の欄は「宣言が全集合」で比べ、差分があれば `client.update`（稼働の表現へ宣言を合成して PUT）。
   新しい分岐は足さず、既存の realm（`webOrigins` が空）で `account-console` の `client.update` 1 件だけになり、当てた後は 0 件であることを試験で固定する。
4. **検査 10 に足す**: `account-console` の `webOrigins` に `+`（または redirect URI の origin を表す値）が無ければ赤。理由の文言に「エッジ越しの token の交換が 403（Invalid origin）」を書く。

## 変更するファイル

- `deploy/keycloak/microservices-platform-realm.json`（account-console の webOrigins）
- `scripts/check-realm-constraints.js`（検査 10・自己試験）・`scripts/keycloak-realm-reconcile.test.js`（既存の realm への追随と冪等）
- `.ai-context/adr/IADR-0524_keycloak-26-upgrade.md`（日付つきの追記）

## PoC への影響

realm の reconcile だけ（Keycloak は再起動しない）。SC-01 の利用者アイコンから SC-16 を開き、個人情報が出ることを確かめる（PR 本文に手順）。

## 検証

- `node scripts/check-realm-constraints.js`: 緑（実データ 0 件）。`--self-test`: 緑（新規 1: `webOrigins` を空・`+` 以外・欄ごと消すと赤）。
- `node scripts/keycloak-realm-reconcile.test.js`: 74 passed（新規 1: 空の稼働へ client.update 1 件・ほかの欄を保つ・当てた後は 0 件）。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`: 1199 passed。`node scripts/k8s-local-up.test.js`: 394 passed。文書の検査: 緑。
- `reconcile-realm.js` は新しい分岐を足していない（ヘッダのコメントに当たり方を書いた）。
