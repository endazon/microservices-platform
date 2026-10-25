---
title: 作業仕様書 — live-rw のリセット申請とアカウントコンソール（SC-15・SC-16・e2e-revocable。E-54）
type: spec
status: in-progress
related_ids: [SC-15, SC-16, SC-13, NFR-12, NFR, ADR-0026, ADR-0045, IADR-0630, IADR-0632, IADR-0633]
author: claude
created: 2026-10-25
updated: 2026-10-25
plan_refs:
  - planning:projects/microservices-platform/05_screens（SC-15・SC-16）
issue: "#2034"
---

# 作業仕様書 — live-rw のリセット申請とアカウントコンソール（#2034 / E-54）

> 本仕様書は実装着手前に作成した（着手 2026-10-25）。親は #2013（網羅表 M-006・M-007、基準書 §5）。
> **範囲は M-006・M-007 だけ。** M-114（SC-22 の試験キー）は #2240 へ切り出した（オーケストレーターの裁定 2026-10-25。
> 使い捨てのスタックに Vault が無く SC-22 は 503 になる。e2e 専用の項目を足すと本番の SC-22 の一覧にも載る）。#2034 の本文は書き換えない。
> 🔴 書き込みは使い捨てのスタック（e2e-live.yml）の中だけ。資格情報の値とリセットリンクを書かない・出さない。

## 受け入れ基準（#2034 ＋ オーケストレーターの条件 2026-10-25）

- [ ] M-006: e2e-revocable（実在）と、`e2e-<runId>-absent-r<retry>`（非実在）でリセットを申請すると、画面の文言が同一（申請者が入れた利用者名の再表示だけを伏せて比べる）。
      文言はテーマの `emailSentMessage`（「メールを送信しました。…」）—— live-rw
- [ ] M-006: 捕捉用 MTA（mailpit）に届くのは実在の側の 1 通だけ（非実在の側は 0 通）。宛先は e2e-revocable の登録アドレス、本文はリセットリンクと有効期限（realm の
      `actionTokenGeneratedByUserLifespan` から導いた 30 分）だけで、他の URL と添付は無い —— live-rw
- [ ] M-006: リセットリンクと鍵の値を記録しない（発行値として `E2E_ISSUED_SECRETS_FILE` へ記録し、成果物の走査の対象にする。trace・スクリーンショット・動画を切る）
- [ ] M-007: e2e-revocable がアカウントコンソールの個人情報を開ける。利用者名は本人に編集不可、属性（`clearance` など）は本人に見えず編集できない。保存は押さない —— live-rw
- [ ] M-007: デバイスの活動に、この試験が作った 2 つのセッションが出る。現在でない方を個別にサインアウトすると、その 1 つだけが消え、そのセッションの画面は
      ログインへ倒れる。現在のセッションと他の役割（e2e-admin の BFF のセッション）は残る —— live-rw
- [ ] 順序: `account.rw.spec.ts` は `live-rw` で流れ、`live-rw-revocation` より前・`live-ro`（auth-errors）より後（L1 で固定）
- [ ] e2e-live で skip 0・失効 6・走査 0、sweep の dry-run で残骸 0・見ていない視点 0（settled の件数を報告）

## 網羅表・API・計画の突き合わせ（着手前）

- SC-15 の申請画面: realm `resetPasswordAllowed: true`。申請後の文言は platform テーマの `emailSentMessage`（存在秘匿。登録の有無によらず同じ）。
- メール: 送出先は捕捉用 MTA（`deploy/local/infra/mailpit.yaml`。エッジへは出さない）。本文の要件（リンクと有効期限だけ）は ADR-0045 決定 7、
  判定は `scripts/check-password-reset-mail.js` の `evaluateResetMail`（純関数）を**そのまま使う**（判定を複製しない）。
- SC-16: Keycloak 26.7.4 のアカウントコンソール（`keycloak.v3`）。realm `editUsernameAllowed: false`、user profile の `unmanagedAttributePolicy: ADMIN_EDIT`
  （`clearance` などの属性は本人に見えない）。ロールを本人が編集する画面・API はアカウントコンソールに無い。
- デバイスの活動: 現在でないセッションだけに「サインアウト」があり、確認（「確認」）の後に `DELETE /realms/platform/account/sessions/{id}` を送る。
- 🟡 **網羅表 M-006 の「12 文字 3 種・直近 5 世代の文言」は範囲外にする**: 送信前のどの画面にも出ない。申請の画面とメールには無く、
  リンクの先の新パスワードの画面（Keycloak 26.7.4 の `keycloak.v2/login/login-update-password.ftl`）も送信前にはポリシーを出さない
  （画面内の検査は長さ・文字種の数の誤りを入力の後に出すだけで、3 種＝regexPattern と 5 世代＝passwordHistory はサーバーの検査で送信の後だけ）。
  オーケストレーターの案（リンクを開き、送信せずに文言を確かめる）を調べた結果、文言に届かないので範囲外のままにした（IADR-0633 決定 2 の理由）。
  ポリシーそのものは `check-realm-constraints.js`（SC-15 試験仕様の T-01〜T-04）が静的に、表示は T-15（手動）が持つ。網羅表 #2013 へ訂正を残した。

## 判断（IADR-0633）

1. **配置**: 新規 `e2e/live/account.rw.spec.ts` を `live-rw` に置く（`live-rw-revocation` は `live-rw` に依存するので、無効化はこの後。IADR-0632）。
   `live-rw` は同じ実行の `live-ro` に依存する（IADR-0630 決定 4）ので、auth-errors（誤りのログイン・ログアウト）とは並ばない。
2. **M-006 はパスワードを変えない**（「申請 → メールが届く」まで）。リンクは開かない。後で流れる revocation（と次の実行の setup・auth-errors）が使う
   e2e-revocable の資格情報はそのままで、整合を取る作業が要らない。リンクは使わずに残る（30 分で切れる。スタックごと消える）。
3. **リンクの値を残さない**: メール本文は spec の中で読み、リンクと `key` を読んだ直後・判定の前に `recordIssuedSecret`（`E2E_ISSUED_RESET_LINK`・
   `E2E_ISSUED_RESET_KEY`）で記録する（成果物の走査が探す）。このファイルは trace・スクリーンショット・動画を切る。失敗の文言には件数と宛先だけを出す
   （`evaluateResetMail` はリンクを文言に入れない。リンク以外の URL は出す）。
4. **mailpit は kubectl で読む（読み取りだけ）**: `kubectl --context k3d-<E2E_STACK> -n platform-infra exec deploy/mailpit -- wget …/api/v1/…`。
   context を引数で固定する（current-context に頼らない）。層 `rw` の門（`assertDisposableStack`）が使い捨てのスタックであることを先に確かめている。
   メールを消す操作はしない（スタックごと消える）。
5. **存在秘匿の比べ方**: 非実在 → 実在の順に申請し、実在の 1 通が届いた後も静かな窓（10 秒）のあいだ件数を見続け、「前 ＋ 1」から
   動かないこと・増えた 1 通が実在の宛先であることで非実在の 0 通を確かめる（届く順には頼らない。10 秒の根拠は IADR-0633 決定 5）。
   画面の文言は `body` の innerText から、入れた利用者名を伏せて比べる。
6. **M-007 は読み取り中心**: 個人情報は開いて見るだけ（保存しない）。書き込みは、**この試験が自分で作った**セッション B の個別サインアウトだけ
   （消えたことを確かめる）。終わりに A・B を Keycloak のログアウトで閉じる（判断 8）。
7. **ログインは毎回新しく**（setup の state は auth-errors のログアウトで失効済み）。OTP は次の周期を待ち、誤りは送らない（IADR-0632 決定 2 と同じ）。
8. **B の行の特定**: 現在でなく、BFF のクライアントを含まない行（アカウントコンソールだけのセッション）がちょうど 1 つであることを求める
   （e2e-revocable の Keycloak には setup・auth-errors の BFF のセッションが残りうる）。終わりに A・B を Keycloak のログアウトで閉じる。

## 変更するファイル

- 新規 `e2e/live/account.rw.spec.ts`・`e2e/live/support/mailpit.ts`（kubectl 経由の読み取り）・IADR-0633
- `e2e/live-opt-in.smoke.spec.ts`（L1: `account.rw.spec.ts` が `live-rw` に入り、`live-rw-revocation` に入らない）・`scripts/test-traceability-allowlist.json`（SC-15 を pending から外す。ratchet の要求）・`src/knip.jsonc`（`ignoreBinaries: kubectl`。OS の道具）・`docs/tests/E2E_STANDARDS.md`・`.ai-context/adr/README.md`

## 検証

- L1 `e2e/live-opt-in.smoke.spec.ts`: 30 passed（新規 1: `account.rw.spec.ts` の M-006・M-007 は `live-rw` に載り、`live-rw-revocation` に載らない）。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`: 1199 passed（test-traceability の ratchet が SC-15 の削除を要求した）。
- `tsc -p tsconfig.node.json` 0 件・eslint 0 件（警告 0）・prettier 緑。`check-knip --require` は既知の unlisted +1（手元に AST の submodule が無い）だけ。
- 文書の検査（check-trace-blocks / check-doc-links / check-doc-updated / check-cross-repo-refs / check-plan-id-qualification / check-reading-budget /
  check-adr-numbering / check-test-traceability / gen-knowledge-graph --check）: 緑。
- live: e2e-live（PR）の結果は PR に書く。
