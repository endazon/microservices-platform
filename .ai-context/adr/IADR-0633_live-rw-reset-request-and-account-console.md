---
title: IADR-0633 live-rw のリセット申請はパスワードを変えずメールの受信までとし、リンクは発行値として記録する。アカウントコンソールは読み取り中心で、書き込みは自分で作ったセッションの個別サインアウトだけ
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-12, SC-13, SC-15, SC-16, ADR-0026, ADR-0045, IADR-0630, IADR-0632]
author: claude
created: 2026-10-25
updated: 2026-10-25
related_specs:
  - ../specs/20261025_2034_live-rw-account.md
---

# IADR-0633: live-rw のリセット申請とアカウントコンソール（#2034）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-25
- 決定者: claude（#2034 / E-54。範囲と条件はオーケストレーターの裁定 2026-10-25: M-006・M-007 だけ。M-114 は #2240 へ切り出し。
  リセットは e2e-revocable で live-rw に置き、リンクとトークンの値を記録しない。パスワードを変えるかは実装の前に仕様書へ書く。
  アカウントコンソールは読み取り中心）

## 起点・関連

- 起点 issue: #2034（親 #2013 の網羅表 M-006・M-007）。M-114（SC-22 の試験キー）は #2240。
- 関連: [IADR-0630](./IADR-0630_live-rw-disposable-stack-gate-and-run-id.md)（live-rw の門・発行値の記録）・
  [IADR-0632](./IADR-0632_live-rw-revocation-last-project.md)（e2e-revocable の失効の試験は最後）。
- 作業仕様書: [20261025_2034_live-rw-account](../specs/20261025_2034_live-rw-account.md)。

## コンテキストと課題

M-006 は SC-15 のリセット申請の存在秘匿とメールを、M-007 は SC-16 のアカウントコンソールを、e2e-revocable で live に流す。
e2e-revocable の資格情報は、同じ実行の revocation（最後の project）と、次の実行の setup・auth-errors も使う。リセットリンクは
それだけでパスワードを変えられる認証資格であり、捕捉用 MTA（mailpit）はエッジへ出していない。アカウントコンソールの書き込み
（個人情報の保存・セッションのサインアウト）は、他の試験が使うセッションを消しうる。

## 決定

1. **配置**: `e2e/live/account.rw.spec.ts` を `live-rw` に置く（`live-ro` の auth-errors の後、`live-rw-revocation` の前）。
2. **M-006 はパスワードを変えない**: 申請 → メールが届くまで。リンクは開かない。網羅表の「12 文字 3 種・直近 5 世代の文言」は
   **送信前のどの画面にも出ない**ので、この試験では測らない（静的には `check-realm-constraints.js`、表示は SC-15 試験仕様の T-15。
   網羅表 #2013 へ訂正を残した）。調べた結果は下の「理由」に置く。
3. **リンクの値を残さない**: 本文は spec の中で読み、判定の前にリンクと `key` を `recordIssuedSecret`（`E2E_ISSUED_RESET_LINK`・
   `E2E_ISSUED_RESET_KEY`）で記録する。ファイルは trace・スクリーンショット・動画を切る。メールの判定は `check-password-reset-mail.js` の
   `evaluateResetMail` をそのまま使う（判定を複製しない。失敗の文言にリンクを入れない）。
4. **mailpit は kubectl で読むだけ**: `kubectl --context k3d-<E2E_STACK> … exec deploy/mailpit -- wget`（Pod の中の loopback）。context は
   引数で固定し、current-context に頼らない。メールは消さない（使い捨てのスタックごと消える）。
5. **存在秘匿**: 非実在（`e2e-<runId>-absent-r<retry>`。何も作らない）→ 実在の順に申請し、申請後の表示を入れた利用者名だけ伏せて比べる。
   実在の 1 通が届いた後も**静かな窓（10 秒）**のあいだ mailpit の件数を見続け、「前 ＋ 1」から動かないこと、増えた 1 通が実在の宛先で
   あることを見る。**届く順には頼らない**（遅れて届く非実在の側の送出も窓の中で落ちる）。10 秒は、既存の存在秘匿の検査
   （`check-password-reset-mail.js` の T-10）が非実在の申請の後に置く待ち（3 秒）の 3 倍を超える長さで、非実在の側は実在の申請と受け取りの
   時間の分だけさらに長く待たれる。
6. **M-007 は読み取り中心**: 個人情報は開いて見るだけ（保存しない）。利用者名は読み取り専用、realm 宣言で管理者が付けた属性は本人の面に
   名前も値も出ない。書き込みは、この試験が作ったセッション B の個別サインアウトだけ。B の行は「現在でない・BFF のクライアントを含まない」行で特定し、
   ちょうど 1 つであることを求める。終わりに A・B を Keycloak のログアウトで閉じる（再試行で前の試行のセッションが B の行と紛れない）。
7. **ログインは毎回新しく**、OTP は次の周期を待ち、誤りは送らない（IADR-0632 決定 2 と同じ）。

## 理由

- 決定 2: パスワードを変えると、後の revocation と次の実行の setup・auth-errors が使う資格情報（Secret `e2e-accounts`）と食い違う。
  変えた後に戻すには同じ試験の中で 2 回変える必要があり、`passwordHistory(5)` に当たる。変えないなら整合を取る作業が要らない。
- 決定 2（文言を範囲外にした根拠。オーケストレーターの指示 2026-10-25 で「リンクを開き新パスワードの画面で文言を確かめ、送信しない」案を調べた）:
  - Keycloak 26.7.4 の `keycloak.v2/login/login-update-password.ftl`（platform テーマの親）は、送信前にポリシーの文言を出さない。
    `password-validation.ftl` の画面内の検査は `length`・`maxLength`・`lowerCase`・`upperCase`・`digits`・`specialChars` だけを、入力の
    `change` で誤りとして出す。realm のポリシーは `length(12) and passwordHistory(5) and regexPattern(…)` で、3 種（regexPattern）と
    5 世代（passwordHistory）はサーバーの検査で、**送信した後**にしか出ない。platform テーマもこの文言を上書きしていない
    （`messages_ja.properties` は SC-13〜15 の 9 語だけで、ポリシーの語は無い）。よって「送信しない」案では文言に届かない。
  - リンクを開くこと自体の影響（参考に調べた）: reset-credentials フローの `ResetPassword`（UPDATE_PASSWORD）と `ResetOTP`（CONFIGURE_TOTP。
    既定のフローは設定を持たないので OTP の資格情報は消さない）は、どちらも**認証セッション**へ required action を足す
    （`context.getAuthenticationSession().addRequiredAction`）。利用者には残らない。ただし文言に届かないので、開く意味が無い。
- 決定 3: trace を切っても、html レポートのエラーメッセージ・attach に値が紛れ込む経路は残る（IADR-0630 決定 6）。記録すれば、
  成果物を上げる前の走査が拾う。
- 決定 6: e2e-revocable の Keycloak には setup・auth-errors の BFF のセッションが残りうる。個別サインアウトをそれらに当てると、
  何を消したかが試験の外から読めない。自分で作った、クライアントで見分けられるセッションだけに当てる。

## 影響

- e2e-revocable の状態を使う試験が 1 本増える（`live-rw`）。brute force の数えは増やさない（誤りを送らない）。
- 捕捉用 MTA にリセットのメールが 1 通残る（リンクは使われずに 30 分で切れる。スタックごと消える）。
- SC-22（M-114）は #2240 で、使い捨てのスタックに秘密情報の保管先が入った後に扱う。
