---
title: IADR-0624 live 層の Playwright config は E2E_LIVE=1 でだけ project を持ち、globalSetup の門（exit 2）と、秘密を残さない setup で storageState を作る
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-10, ADR-0026, ADR-0032, ADR-0090, IADR-0569, IADR-0570, IADR-0294]
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0032（SPA は BFF セッション。トークンを扱わない）
  - planning:projects/microservices-platform/07_adr/ADR-0090（門は依存ごと。全 skip を緑にしない）
related_specs:
  - ../specs/20261024_2025_live-playwright-config.md
---

# IADR-0624: live 層の config・setup・readiness 門（#2025）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-24
- 決定者: claude（#2025。親 #2013 の E-31）

## 起点・関連

- 起点 issue: #2025（親 #2013。設計はコメント 3 §1〜§2）。関連する計画書 ID: NFR、NFR-10（MFA）。
- 関連する実装判断: [IADR-0569](./IADR-0569_e2e-layers-and-live-opt-in.md)（層・project 名・`E2E_LIVE` / `E2E_LAYER`）・
  [IADR-0570](./IADR-0570_e2e-accounts-placeholder-credentials.md)（試験アカウント 6 名と資格情報の供給）・
  [IADR-0294](./IADR-0294_mfa-enforcement-and-audit-events.md)（MFA 必須）。
- 作業仕様書: [20261024_2025_live-playwright-config](../specs/20261024_2025_live-playwright-config.md)。

## コンテキストと課題

IADR-0569 が名前（config・project・環境変数）を決め、IADR-0570 が試験アカウントと資格情報の供給を置いた。稼働の環境で流す config と、
6 役割のログインで storageState を作る setup、前提が欠けたときの扱いが無い。live の実行は稼働の Keycloak に本物のパスワードと OTP を
入れ、BFF のセッション cookie を受け取るので、**成果物に秘密が残る経路**を塞ぐ必要がある。

## 検討した選択肢

| 論点 | 案 | 採否 |
| --- | --- | --- |
| 前提の門の置き場 | A. setup project の最初の test | 採らない。門の不成立が「setup の失敗（赤）」になり、製品の不具合と区別できない |
| | **B. `globalSetup`。不成立なら exit 2** | **採る**。test を 1 件も流さずに終わる |
| `E2E_LIVE` が無いとき | A. config で例外 | 採らない。opt-in しないのは正常 |
| | **B. project を 0 件にし `--pass-with-no-tests` で exit 0** | **採る**（IADR-0569 決定 4） |
| setup の trace | A. 基準書の表どおり `retain-on-failure` | 採らない。trace は `fill` の値（パスワード）と `Set-Cookie` を記録する |
| | **B. `off`（スクリーンショット・動画も）。失敗した段は文字で attach** | **採る** |
| パスワードの入力 | A. `locator.fill(value)` | 採らない。**値が step の題に載り、html レポートに残る**（偽の BFF と Keycloak に対する実測で 6 名分が出た） |
| | **B. `evaluate` で値をページの中で入れ、input / change を起こす（`fillSecret`）** | **採る**。実測でレポート・test-results に値が 0 件 |
| TOTP の計算 | A. e2e に実装を置く（`e2e/keycloak/support.ts` と同じ形） | 採らない。2 つ目の実装になる |
| | **B. `scripts/lib/totp.js` を `createRequire` で読む** | **採る**（RFC 6238 のテストベクタで固定済みの 1 つを使う） |

## 決定

1. `playwright.live.config.ts`: `E2E_LIVE=1` のときだけ `live-setup` と `E2E_LAYER` で選んだ層の project を持つ。それ以外は 0 件。
   値域外の `E2E_LAYER`・`E2E_LIVE=1` 無しの `E2E_LAYER` は読み込みで例外。判定は `e2e/live/support/env.ts` の純関数。
2. `globalSetup`（`e2e/live/support/readiness.ts`）: 資格情報の環境変数 12 個（名前だけを報告）・Keycloak の discovery 200・BFF の
   `/bff/auth/me` 401 を確かめ、満たさなければ **exit 2**。
3. `e2e/live/auth.setup.ts`: 6 役割で 1 回ずつ実ログインし、`/bff/auth/me` のロール（`platform-admin` / `platform-operator` / `trading-owner`）が
   その役割の宣言と一致することを確かめてから、storageState を `E2E_AUTH_DIR`（既定 `src/platform/frontend/playwright/.auth/`。gitignore）に書く。
   trace・スクリーンショット・動画は切り、秘密は `fillSecret` で入れる。`retries: 0`。
4. L1 の `playwright.config.ts` は `e2e/live/` を `testIgnore` する。opt-in・門・TOTP の振る舞いは L1 の `e2e/live-opt-in.smoke.spec.ts` が
   子プロセスで live の config を起動して固定する（稼働の環境に触れない）。

## 理由

- 門を test の外に置くと、「前提未整備」「製品の失敗」「流していない」の 3 つが終了コードで分かれる（ADR-0090）。
- 秘密は「書かない」だけでなく「道具が勝手に書く経路」を塞がないと残る。`fill` の題と trace はどちらも既定で値を残す。

## 影響

- `pnpm --filter @platform/frontend run test:e2e:live` で流す。稼働の環境の値は Secret `platform-infra/e2e-accounts` から取る（PR 本文の手順）。
- spec 側は `stateFile(authDir, role)` を `test.use({ storageState })` に渡して役割を選ぶ（E-33 以降）。
- live の spec の project（`live-ro` など）は trace を `retain-on-failure` で残す。その trace はリクエストの Cookie ヘッダを含みうるので、
  成果物に上げる前の検査は E-32（`e2e-live.yml`）の受け入れ基準で扱う。

## 残余リスク

- 稼働の Keycloak・BFF に対する実ログインはこの PR では流していない（偽の BFF と Keycloak に対して 6 名分の state と、秘密が成果物に出ないことを確かめた）。
- `fillSecret` はフォームの値を直接入れる。Keycloak のテーマが入力欄を JavaScript で作り直す形に変わると効かなくなりうる（そのときは OTP 画面へ進めず setup が赤になる）。
