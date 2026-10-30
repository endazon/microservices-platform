---
title: 作業仕様書 — M-005 初回 TOTP セットアップを使い捨て k3d でだけ流す（#2040 / E-81）
type: spec
status: in-progress
related_ids: [NFR, NFR-10, SC-14, ADR-0026, IADR-0630, IADR-0662]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2040"
---

# 作業仕様書 — 初回 TOTP 登録の live の試験（#2040）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `7514f71e`）。#2013 の分割表 E-81（波 7・任意・P2）。

## 範囲

- 網羅表 M-005（SC-14。初回セットアップ）。触る領域（issue）: `e2e/live/totp-enroll.ro.spec.ts`（`E2E_DISPOSABLE=1` 門）。
- 門のために足す:
  - `e2e/live/support/env.ts`（`E2E_DISPOSABLE`）・`projects.ts`（未設定なら外す）・`readiness.ts`（向け先の門）。
  - `e2e/live/support/keycloakAdmin.ts`（一時アカウントの作成・削除）。`login.ts`（`fillSecret` を公開）・`totp.ts`（`base32Of`）。
  - L1 の `e2e/live-opt-in.smoke.spec.ts`（門の試験）、`.github/workflows/e2e-live.yml`（`E2E_DISPOSABLE: "1"`）、`env.e2e.example`。
- 設計の決定と却下案は IADR-0662。

## 受け入れ基準

- [x] 常設 PoC では 0 件。
  - `E2E_DISPOSABLE` が無い `--list` に `totp-enroll` が出ない。`=1` なら live-ro に 2 件載る（L1 の試験で固定）。
- [ ] 使い捨てのスタック（e2e-live）で M-005 の 2 件が通る（PR の e2e-live の実行で確かめる）。

## リカバリーコード（M-005 の後半）

- Keycloak 26.7.4 の UpdateTotp を読んだ。OTP の登録の後にリカバリーコードへ進めるのは、次の 2 つがそろうときに限る。
  - `CONFIGURE_TOTP` の設定 `add-recovery-codes` が真（既定 false）。
  - 認証フローにリカバリーコードの段がある。
- realm の宣言は `config: {}`・`browserFlow` 未宣言で、どちらも満たさない。→ 現状は表示されない。
- 画面仕様書の「する」（「Keycloak 本体の既定挙動に依存」）は誤り。訂正する。
- 直す（realm の変更）かは判断を仰ぐ（IADR-0662 決定 4）。

## 母集合（規則 9・10）

- **規則 9**: 次の語で `*.md`・`*.ts`・`*.js`・`*.yml` を全文走査した。
  - 語: 「リカバリーコード」「T-06」「T-09」「E2E_STACK が設定されているのに」「live-rw は使い捨てのスタックの中だけ」「liveProjects(」
  - `docs/screens/SC-14_otp-mfa.md` の「リカバリーコードを登録完了時に 1 回のみ表示 | する」: 誤りなので訂正する。
  - `docs/tests/SC-14_otp-mfa.md` の T-06（手動）・T-09・未決事項: T-06 は自動へ、T-09 は未達を書く。
  - `docs/tests/E2E_STANDARDS.md` の §6 の opt-in の箇条: `E2E_DISPOSABLE` を足す。
  - `e2e/live-opt-in.smoke.spec.ts` の `/E2E_SWEEP も層の rw も無い/`: 文言を変えたので追随する。
  - `docs/screens/SC-16_account-settings.md` のリカバリーコードの再発行: アカウントコンソールの話で、本件と別。追随しない。
  - `.ai-context/adr/IADR-0197` の「リカバリーコードの表示フローを実装する」（残作業）: 凍結記録。現状と整合しているので書き換えない。
- **規則 10**（この変更で新たに誤りになる自分の記述）:
  - readiness の門の注記「`rw` が無ければ何もしない」→ `E2E_DISPOSABLE` も足した。
  - E2E 基準書 §2.1 の project 表（live-ro の testMatch）は変えていない。外すのは旗が無いときの testIgnore だけ。

## 検証

- L1: `playwright test e2e/live-opt-in.smoke.spec.ts --project smoke`（35 件）。
- prettier・eslint・typecheck（`platform/frontend`）。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`（コミットした状態で）。
- 文書系の検査。
- e2e-live（PR）で M-005 の 2 件。
