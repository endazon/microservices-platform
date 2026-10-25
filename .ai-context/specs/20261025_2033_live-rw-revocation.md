---
title: 作業仕様書 — live-rw の利用者の無効化と全セッション失効（SC-17・e2e-revocable。E-53）
type: spec
status: done
related_ids: [SC-17, SC-13, NFR-14, UC-05, FR-09, NFR, ADR-0032, IADR-0626, IADR-0630, IADR-0632]
author: claude
created: 2026-10-25
updated: 2026-10-25
plan_refs:
  - planning:projects/microservices-platform/05_screens（SC-17）
issue: "#2033"
---

# 作業仕様書 — live-rw の利用者の無効化と全セッション失効（#2033 / E-53）

> 本仕様書は実装着手前に作成した（着手 2026-10-25）。親は #2013（網羅表 M-009・M-109、基準書 §5）。E-51 / E-52 の部品（門・`cleanup.ts` など）を使う。
> 🔴 書き込みは使い捨てのスタック（e2e-live.yml）の中だけ。資格情報の値を書かない・出さない。

## 受け入れ基準（#2033 ＋ オーケストレーターの条件 2026-10-25）

- [x] M-009: e2e-admin が SC-17 で e2e-revocable を無効化すると、別 context でログインしていた e2e-revocable の `/bff/auth/me` と業務の API（`GET /bff/private-notes`）が 401 になり、保護画面は `/login` へ倒れる。再有効化の後も旧セッションのままは 401 —— live-rw
- [x] M-109: e2e-admin が SC-17 で e2e-revocable を再有効化すると「有効」に戻る（セッションは戻らない）—— live-rw
- [x] afterEach で e2e-revocable が有効に戻っていることを API で確かめ、戻っていなければ有効化を送り、それでも戻らなければ赤 —— live-rw
- [x] e2e-revocable を失効させる試験は、他の試験が e2e-revocable の状態を使い終わってから流れる（project の依存で固定。L1 で固定）
- [x] e2e-live で skip 0・失効 6・走査 0、sweep の dry-run で残骸 0・見ていない視点 0（settled の件数を報告）

## 網羅表・API・計画の突き合わせ（着手前）

- `POST /bff/admin/users/{userId}/disable`: 「無効化と全セッション失効は 1 つの操作」（バックチャネルログアウトで BFF のセッションが subject 単位で消える）。
- `POST /bff/admin/users/{userId}/enable`: 「セッションは復活しない。本人が改めてログインする」。
- 計画 SC-17（画面の文言）: 無効は「無効（全セッション失効）」、無効化の前に確認を挟む。網羅表 M-009・M-109 と食い違いは無い。

## 判断（IADR-0632）

1. **順序を project の依存で固定する**: `revocation.rw.spec.ts` は層 `rw` の project（`live-rw`）から外し（`testIgnore`）、専用の project
   `live-rw-revocation` で流す。`live-rw-revocation` は同じ実行で選んだ `live-ro`・`live-rw` の両方に依存する。e2e-revocable の状態を使う他の試験
   （live-ro の auth-errors＝ログインの異常系とログアウト、E-54 の SC-15 リセット申請・SC-16 アカウントコンソール）は `live-ro`・`live-rw` に
   置くので、無効化は必ずその後になる。L1 が project の組み立ての純関数（`e2e/live/support/projects.ts` の `liveProjects`）を直接呼び、依存を固定する
   （Playwright の JSON 出力の config は project の依存を含まないので、純関数へ分けた）。
2. **e2e-revocable は新しくログインする**: setup の state のセッションは auth-errors のログアウト（バックチャネルで subject の全セッション）で
   既に失効している。別 context でパスワードと**次の周期の** OTP を入れてログインする（`otpPolicyCodeReusable: false`。直前の試験が同じ周期の
   6 桁を使っていても重ならない）。誤りは送らない（brute force の数えを増やさない。auth-errors は誤りの直後に正しいログインで数えを戻している）。
3. **秘密を入力するので、このファイルは trace・スクリーンショット・動画を切る**（auth-errors と同じ）。
4. **後始末は有効化**（作るものは無いので `cleanup.ts` は使わない）。afterEach が `GET /bff/admin/users` で e2e-revocable の `enabled` を見て、
   偽なら `POST …/enable` を送り、もう一度読んで真でなければ赤にする（`cleanup.ts` は失敗しても test を落とさないので、ここでは使わない）。
5. **teardown**: e2e-revocable の state の cookie は既に失効している。teardown は `/bff/auth/me` が 401 なら失効済みとして数える（失効 6 のまま）。

## e2e-revocable を使う試験の順序と影響

| 順 | project | 試験 | e2e-revocable の状態 |
| --- | --- | --- | --- |
| 1 | live-setup | auth.setup.ts | ログイン（state を書く） |
| 2 | live-ro | auth-errors.ro.spec.ts（M-003・M-004・M-010） | 誤りのパスワード 1・誤りの OTP 1 → 正しいログインで数えを戻す → ログアウト（全セッション失効） |
| 3 | live-rw | （E-54 で SC-15・SC-16 を足す） | ログイン・アカウントコンソール |
| 4 | live-rw-revocation | revocation.rw.spec.ts（M-009・M-109） | 新しくログイン → 無効化（全セッション失効）→ 再有効化 |
| 5 | teardown | — | `/bff/auth/me` が 401 → 失効済みとして数える |

- brute force（5 回の失敗で 900 秒）: 順 2 の誤りは 2 回で、直後の成功で数えが戻る。順 4 は誤りを送らない。
- state の失効: 順 2 のログアウトで setup の state は使えなくなる。順 4 は state を使わない。

## 変更するファイル

- 新規 `e2e/live/revocation.rw.spec.ts`・IADR-0632
- 新規 `e2e/live/support/projects.ts`（project の組み立てを config から分けた純関数）・`playwright.live.config.ts`・`scripts/scripts.repo.test.js`（層の対応表の読み先）・`e2e/live-opt-in.smoke.spec.ts`（L1: 順序）・`docs/tests/E2E_STANDARDS.md`・`.ai-context/adr/README.md`

## 検証

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`: 1199 passed（層 → project 名の対応表の読み先を `support/projects.ts` へ改めた）。
- L1 `e2e/live-opt-in.smoke.spec.ts`: 29 passed（新規 2: `live-rw-revocation` の依存は `live-setup`・`live-ro`・`live-rw`、`live-rw` は失効の試験を流さない／資格情報が欠けたら変数名だけの例外で止める）。
- 資格情報の読み出しを `credentialsOf`（`support/env.ts`）へ寄せた: 3 ファイル 6 箇所（`auth.setup.ts`・`auth-errors.ro.spec.ts`・`revocation.rw.spec.ts` の `process.env[...] ?? ''`）。欠けていれば Keycloak へ送る前に止まる（#2239 のレビュー）。
- `E2E_LIVE=1 E2E_LAYER=ro,rw E2E_STACK=e2e-live … --list`: 53 件（`[live-rw-revocation] › revocation.rw.spec.ts`）。
- `tsc -p tsconfig.node.json` 0 件・eslint 0 件・prettier 緑。文書の検査は緑。
- live（head 3fae7b42 の e2e-live、run 40159057048、層 ro,rw）: 53 passed・skip 0（`check-e2e-skips`: 全 skip の project は無い）。`[live-rw-revocation] › revocation.rw.spec.ts` が通り、afterEach の有効化の確認も通った。teardown が失効させたのは 6 役割（user・operator・admin・noaccess・revocable・trading-owner）。成果物 2 ファイルを走査し、資格情報 12 個と発行値 2 個の値はどちらも 0 件。sweep の dry-run は残骸 0 件・見ていない視点 0 件で、後始末済みは 2 件（datasources 1・mcp-clients 1。どちらも無効化済みで残骸に数えない）。
