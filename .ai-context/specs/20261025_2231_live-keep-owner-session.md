---
title: 作業仕様書 — live の teardown に取引オーナーのセッションだけを残す opt-out（E2E_KEEP_SESSIONS=trading-owner）
type: spec
status: done
related_ids: [NFR, ADR-0032, IADR-0624, IADR-0625, IADR-0628]
author: claude
created: 2026-10-25
updated: 2026-10-25
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0032（SPA は BFF セッション）
issue: "#2231"
---

# 作業仕様書 — live の teardown に取引オーナーだけを残す opt-out（#2231）

> 本仕様書は実装着手前に作成した（着手 2026-10-25）。AST の E-33（AST#1416 / AST#1476・AST/IADR-0588）の前提である。
> 🔴 稼働中の PoC には触れない。値を書かない。

## 受け入れ基準（#2231）

- [x] `E2E_KEEP_SESSIONS=trading-owner` のとき、偽の BFF と Keycloak に対して teardown が 5 回ログアウトし、`trading-owner.json` だけが 0600 で残る（L1）
- [x] 値域外・`E2E_LIVE=1` 無しは読み込みで失敗する（L1）
- [x] 未設定なら従来どおり 6 回ログアウトして state が残らない（L1。既存の試験）
- [x] `e2e-live.yml` が `E2E_KEEP_SESSIONS` を使っていない（L1）

- [x] （レビュー 🟢 で追加）残す役割の state の権限を変えられなくても、他の 5 役割は 5 回とも失効させ、失敗に積む（exit 1）（L1）

## 判断（IADR-0628）

1. 値域は `trading-owner` だけ（`KEEPABLE_ROLES`）。`1`・`all`・他の役割・カンマ区切りは例外。全部を残す指定は作らない。
2. 残すのは失効とファイルの削除だけを飛ばすこと。残すファイルは 0600。setup は全役割の state を書いた直後に 0600 にする（cookie を持つファイル）。
3. 既定（未設定）は従来どおり全部失効。`e2e-live.yml` は使わない（使い捨てスタックは AST の live を流さない）。成果物の走査の門は変えない。
4. 残したセッションの後始末は AST の live の teardown（AST/IADR-0588 決定 4）。

## 流す順（常設の PoC。PoC セッションが行う）

1. MSP: `E2E_LIVE=1 E2E_KEEP_SESSIONS=trading-owner pnpm --filter @platform/frontend exec playwright test --config playwright.live.config.ts --project live-setup`
2. AST: `cd frontend && E2E_LIVE=1 E2E_AUTH_DIR=<MSP>/src/platform/frontend/playwright/.auth npx playwright test --config playwright.live.config.ts`
3. AST の teardown が取引オーナーを失効させ state を消す。

## 検証

- `tsc`・`eslint`・`prettier`、`live-opt-in.smoke.spec.ts`（19 passed。新規 4）。
- knip: 当初 `KEEPABLE_ROLES` を export して床（exports）を 1 件超えた（PR #2232 の build-test）。モジュールの中だけで使うので export を外した（床は上げない）。
