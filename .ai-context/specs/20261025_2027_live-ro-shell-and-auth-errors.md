---
title: 作業仕様書 — [E-40] live-ro: 認証・ナビ・NotFound の 4 役割
type: spec
status: in-progress
related_ids: [NFR, NFR-10, NFR-13, SC-05, SC-09, SC-13, SC-14, IADR-0035, IADR-0273, IADR-0337, IADR-0624, IADR-0625, IADR-0626]
author: claude
created: 2026-10-25
updated: 2026-10-25
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md（§共通シェル・SC-13/14・SC-05・SC-09 のアクセス制御）
  - planning:projects/microservices-platform/07_adr/ADR-0032（SPA は BFF セッション。トークンを扱わない）
  - planning:projects/microservices-platform/07_adr/ADR-0026（認証は IdP。MFA 必須）
issue: "#2027"
---

# 作業仕様書 — live-ro: 認証・ナビ・NotFound の 4 役割（E-40）

> 本仕様書は実装着手前に作成した（着手 2026-10-25）。親は #2013（網羅表はコメント 2 の A 節、流し方はコメント 3 §1〜§4）。
> 先行の E-31（#2025・IADR-0624）が live 用 config・6 役割の storageState・teardown を、E-32（#2026・IADR-0625）が `e2e-live.yml`
> （使い捨ての k3d スタック）を置いた。🔴 **稼働中の PoC には触れない**。live の実行は PR の `e2e-live`（使い捨てスタック）でだけ確かめる。

## 範囲（網羅表の行）

| 行 | 内容 | 本 PR での置き場 |
| --- | --- | --- |
| M-003 | 誤りのパスワードで固定文言「社員ID またはパスワードが正しくありません」、存在しない ID でも同文言（1 回だけ） | `e2e/live/auth-errors.ro.spec.ts` |
| M-004 | TOTP 不一致で再入力を求め、正しいコードで通る（誤り 1 回のみ） | 同上 |
| M-010 | ログアウトで cookie が消え、保護画面がリロードで出ない | 同上 |
| M-013 | 左ナビの出し分けを live で 4 役割（user / operator / admin / noaccess） | `e2e/live/shell.ro.spec.ts` |
| （受け入れ基準）権限外 NotFound | 運用者の画面（SC-05）と管理者限定の画面（SC-09）を 4 役割で開き、許す側で本体・許さない側で NotFound | 同上 |

## 受け入れ基準（issue #2027）

- [ ] 4 役割のナビ集合 —— `shell.ro.spec.ts` の M-013（L1 と同じ期待値の表）
- [ ] 権限外 NotFound —— 同 spec。両側（許す役割で本体、許さない役割で NotFound）で固定。管理者限定は BFF の 403 / 200 も同じ役割の組で見る
- [ ] 誤 PW・誤 TOTP は 1 回のみで成功ログインで戻す —— `auth-errors.ro.spec.ts`（`retries: 0`・直列）
- [ ] PR の `e2e-live` が緑で、`live-ro` に test が 1 件以上・skip 0、teardown が 6 役割を失効、成果物の走査 0

## 判断（IADR-0626）

1. **認証の異常系とログアウトの被験者を `e2e-revocable` にする**（網羅表は M-003/M-004 を `e2e-noaccess`、M-010 を `e2e-user` としている）。
   BFF の backchannel logout は **subject の全セッション**を失効させる（IADR-0273 決定 2。`BackchannelLogoutProcessor`）。`e2e-user` や
   `e2e-noaccess` でログアウトすると、並列に流れる他の live-ro の spec（E-41・E-42 も同じ役割を使う）の storageState が巻き添えで失効する。
   brute force の数えもアカウントの状態であり、基準書 §5「状態を変える試験は `e2e-revocable`」に揃う。
2. ログインを試す spec は**新しいログイン**（実パスワード・OTP の入力）を行うので、**trace・スクリーンショット・動画を切る**（`live-setup` と同じ理由）。
   失敗の段は文字で attach する。部品（`fillSecret`・`stage`・`submitPassword`・`submitOtp`）は `e2e/live/support/login.ts` へ切り出し、setup と共有する。
3. OTP は**次の周期を待ってから**出す（`otpPolicyCodeReusable: false`）。誤りの OTP は前後 1 周期（`otpPolicyLookAheadWindow: 1`）の正しい値の
   どれとも違う値にする。待つことで誤りのパスワードと誤りの OTP の間が 1 秒以上空き、Keycloak の quick login check（1 秒以内の連続失敗で 60 秒ロック）にも掛からない。
4. 左ナビの期待値の表は L1（`shell.smoke.spec.ts`）から `e2e/support/navExpectations.ts` へ切り出し、L1 と live で同じ表を見る（2 箇所に写さない）。
5. L1 の `live-opt-in.smoke.spec.ts` の偽スタック（SPA を配らない）は `--project live-setup` に絞る（ro の spec が偽スタックへ流れないように）。

## 母集合（規則 9・10）

- 「`fillSecret` の置き場」を引いた: `docs/tests/E2E_STANDARDS.md` §9（`e2e/live/auth.setup.ts` の `fillSecret`）→ `support/login.ts` へ直す。
  `.ai-context/` の IADR-0624・作業仕様書 2025 は凍結記録なので直さない（名前 `fillSecret` は変えていない）。
- 「live の spec はまだ無い」: `E2E_STANDARDS.md` §2.1 → 日付つきで追記する。
- 左ナビの表の写し: `grep -rn "ADMIN_ONLY_ITEMS\|PERSONAL_GROUP"` で `shell.smoke.spec.ts` だけ（他の spec は写していない）。

## 検証

- `pnpm exec tsc -p tsconfig.node.json --noEmit`、`eslint`（e2e）、`prettier --check`。
- `live-opt-in.smoke.spec.ts` 13 件（偽スタックの setup・teardown を含む）。変異: `--project live-setup` を外す → 偽スタックの test が赤、
  `wrongOtpFor` を次の周期の正しい値にする → 赤。
- `E2E_LIVE=1 playwright test --config playwright.live.config.ts --list` で live-ro に 13 件。
- PR の `e2e-live`（使い捨てスタック）で live-ro が緑・skip 0・teardown 失効 6・走査 0。

## 計画への環流

- 無し（網羅表は #2013 の設計で計画書ではない。役割の差し替えは IADR-0626 と #2013 への注記で足りる）。
