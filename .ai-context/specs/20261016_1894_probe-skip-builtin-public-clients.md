---
title: 測定器のクライアント選びから Keycloak の組み込みクライアントを外す（#1894 の追随漏れ。integration-stack の赤）
type: spec
status: done
related_ids: [SC-15, SC-16, SC-13, NFR-09, ADR-0078, IADR-0429, IADR-0546]
author: claude
created: 2026-10-16
updated: 2026-10-16
plan_refs: []
issue: "#1894"
---

# 仕様書: 測定器のクライアント選びから Keycloak の組み込みクライアントを外す

## 起点

- MSP#1944（#1894 / IADR-0546。マージ `d3addc82`）で、realm の宣言へ Keycloak の組み込み `account` / `account-console`（public・redirectUri は相対の `/realms/platform/account/*`）を足した。
- その後の develop の integration-stack が `d3addc82` 以降すべて赤になった（`Self-test the password-reset mail gate`）。直前の `28342843` は緑。
- 原因は 2 つある。
  1. `scripts/check-password-reset-mail.js` の自己試験にある #1413 のラチェット「実データの realm に public client が無い」。MSP#1944 は姉妹のラチェット（`scripts/scripts.repo.test.js` の #1393）にだけ組み込みの例外を足し、こちらを追随させていなかった。この自己試験は PR の CI では走らず、integration-stack でだけ走る。
  2. **実害**: `pickBrowserFlowClient` は「public client を優先」するため、実データでは `account`（相対 redirectUri）を選ぶようになっていた。この関数はパスワードリセットの測定（SC-15）と、ログインの存在秘匿の測定（`check-login-existence-disclosure.js`。SC-13 / ADR-0078）が共用している。測定は製品のログイン経路（`bff`）ではなくアカウントコンソールを叩く形へ黙って変わっていた。存在秘匿の自己試験は緑のままなので、ラチェットが無ければ気付けなかった。

## 母集合（規則 9）

- `git grep -n 'publicClient' -- scripts deploy/local .github` を走査した。
- 「public を優先」「public は無い」を前提に**クライアントを選ぶ**のは `pickBrowserFlowClient`（共用 2 か所）だけだった。
- 次は除外した。いずれも**名指しのクライアント**の属性を見ており、組み込みの追加の影響を受けない。
  - `check-mcp-client-provisioning.js` / `seed-abac-policies.js` / `reset-gate.test.js`: 名指しのクライアントを見る。
  - `check-realm-copy-drift.js`: 接頭辞 `ai-stock-trading-` のクライアントだけを比べる。
  - `check-realm-constraints.js`: 検査 10 は組み込みを前提に MSP#1944 で追随済み。

## 変更

- `pickBrowserFlowClient` の候補から Keycloak の組み込みクライアント（`account`・`account-console`・`admin-cli`・`broker`・`realm-management`・`security-admin-console`）を外す。測りたいのは製品のブラウザログインの経路である。
- 自己試験の #1413 ラチェットに、`scripts.repo.test.js` と同じ例外（`account` / `account-console`）を入れる。あわせて、実データで `bff`（絶対 URI）が選ばれることを固定する。
- 陰性対照: 組み込みの public client は public でも選ばない。組み込みしか無い realm では null（前提なしで止まる）。

## 検証

- `node scripts/check-password-reset-mail.js --self-test`: 60 件 OK。
- `node scripts/check-login-existence-disclosure.js --self-test`: OK。
- 変異（除外を外す）でラチェットが赤になり、戻すと緑になる。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
- 修正後の develop で integration-stack を dispatch して緑を確かめる（マージ後）。
