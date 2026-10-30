---
title: IADR-0662 初回の TOTP 登録（M-005）は使い捨てのスタックだけで、一時アカウントを管理 API で作って消して流す（E2E_DISPOSABLE）
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-10, SC-14, ADR-0026, IADR-0569, IADR-0570, IADR-0624, IADR-0626, IADR-0630]
author: claude
created: 2026-10-30
updated: 2026-10-30
related_specs:
  - ../specs/20261030_2040_totp-enroll-disposable.md
---

# IADR-0662: 初回の TOTP 登録の live の試験（#2040 / E-81）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-30
- 決定者: claude（#2040）

## 起点・関連

- 起点 issue: #2040（#2013 の分割表 E-81。任意・P2）。網羅表の M-005。
  - 内容: 初回セットアップ（QR・手動キー・確認コード、リカバリーコード 1 回表示）。
  - 層: L2（使い捨て k3d のみ）。
  - 前提: `CONFIGURE_TOTP` 付きの一時アカウント。
  - 受け入れ基準: 常設 PoC では 0 件。
- 前提:
  - IADR-0569（層・opt-in）。
  - IADR-0570（試験アカウントと TOTP の生の秘密）。
  - IADR-0624（秘密の入力と成果物）。
  - IADR-0630（書き込みの向け先の門と発行値の記録）。

## コンテキストと課題

- 試験アカウント e2e-* 6 名は OTP の資格情報を宣言で持つ（IADR-0570）。初回の登録の画面へは誘導されない。
  - 一度登録した利用者は二度と誘導されないので、試験には**毎回新しい未登録の利用者**が要る。
- 利用者を作る口は BFF に無い（SC-17 は無効化・属性・ロールだけ）。
  - Keycloak の管理 API はエッジで拒んでいる（`deploy/local/edge/keycloak-admin-deny.yaml`）。
- realm の宣言へ 7 人目の試験アカウントを足す案もある。
  - 宣言は常設の PoC と共有なので、PoC にもパスワードを持つアカウントが増える。
  - 資格情報の数（12 個）を前提にした門と供給の経路（OpenBao → ESO・`gen-e2e-accounts.js`）も変わる。
- Keycloak 26.7.4 の `CONFIGURE_TOTP`（UpdateTotp）は、登録の後にリカバリーコードの登録へ進めるかを、次の 2 つがそろったときに限る。
  - 設定 `add-recovery-codes` が真である（既定は false）。
  - 認証フローにリカバリーコードの段がある。
  - realm はどちらも宣言していない。

## 決定

1. **新しい opt-in `E2E_DISPOSABLE=1`**。使い捨てのスタックでしか流せない試験を有効にする。
   - 値域は `1` か未設定・空で、それ以外は読み込みで例外。`E2E_LIVE=1` と `E2E_STACK` を要る。
   - globalSetup の向け先の門（`assertDisposableStack`。`scripts/lib/disposable-stack.js`）は、層の `rw` に加えてこの旗でも通す。
   - 未設定なら `totp-enroll.ro.spec.ts` を live-ro の `testIgnore` に入れる。**常設の PoC では 0 件**になる（`--list` で固定）。
   - `e2e-live.yml` は `E2E_DISPOSABLE: "1"` で流す。
2. **一時アカウントは spec が Keycloak の管理 API で作って消す**（`e2e/live/support/keycloakAdmin.ts`）。
   - 口は `kubectl --context k3d-<E2E_STACK> port-forward svc/keycloak`。realm の後追いの Job と同じ `http://keycloak:8080` の口・同じ発行者である。
   - 管理者の資格情報は Secret `platform-infra/keycloak-admin` から読む。トークンは呼ぶたびに取り直す（master の寿命は短く、試験は OTP の周期を待つ）。
   - port-forward は自分で起こした子プロセスで、`close()` がその子だけを止める。
   - 名前は `e2e-<runId>-totp-enroll-r<再試行>`。afterAll で消す（404 は消えた扱い）。
3. **作成は realm の既定に任せ、requiredActions を書き足さない**。
   - 作った直後に `CONFIGURE_TOTP` だけが付いていることを見る。新しい利用者が実際に受ける誘導を試す。
   - 見ること: QR（PNG の data URI と描画）、手動入力キー（hidden の `totpSecret` の base32 と一致し、切り替えても秘密が変わらない）、
     方式の表示（6 桁・30 秒）、デバイス名、確認コード。
   - 登録の後、必須アクションの画面で止まらずに元の画面へ戻り、`/bff/auth/me` の subject が作った利用者であること。
   - 登録の後、必須アクションが空になり、資格情報が `otp` と `password` になること。
   - 2 度目のログインは登録の画面を出さず、次の周期の 6 桁だけで通ること。
4. **リカバリーコードの 1 回表示（M-005 の後半・計画 SC-14）はここで見ない**。現状の realm では表示されない（上のコンテキスト）。
   - 表示されるように直すには、realm（`CONFIGURE_TOTP` の設定と browser フロー）の変更が要る。常設の PoC の認証フローを変えるので、別の作業にした。
     - 追跡先: **#2372**（P2）。コーディネーターの判断（2026-10-30）で、計画の誤りではなく実装の未達として切り出した。
   - 画面仕様書の「する」は誤りだったので訂正した。
   - 表示されるようになったときの検知は **2 重**にする（［2026-10-30 / #2371 の AI レビュー 🟡］従前は「必須アクションの画面で止まり赤で知らせる」とだけ
     書いていた。それは必須アクションの path で止まる経路にしか効かない）。
     - (a) 登録の後、**元の画面（エッジ）へ戻る**こと（`expectBackOnEdge`）。Keycloak のどの path の画面で止まっても、エッジへ戻らなければ赤になる。
     - (b) 管理 API の資格情報の種類が `otp`・`password` に**完全一致**すること（並べ替えた配列の `toEqual`）。
       リカバリーコードが作られれば `recovery-authn-codes` が増えて赤になる。画面を経ずに作られる経路も捕まえる。
     - 赤になったら後半（1 回だけの表示・2 度目に出ないこと）を足す。直す作業そのものは #2372 で扱う。
5. **層は ro のまま**（issue の指定どおり `*.ro.spec.ts`）。
   - 書き込みは、使い捨てのスタックの中で自分が作った一時アカウントと、そのアカウントの OTP の資格情報だけである。
   - 共有の試験アカウント・業務のデータには触れないので、同じ実行の他の試験を揺らさない。
6. **秘密を残さない**。
   - spec は trace・スクリーンショット・動画を切る。パスワードと TOTP の秘密（生の文字列と base32）は作った・読んだ直後に発行値として記録する。
   - 値どうしの比較は真偽で行う（期待値の比較に渡すと失敗の文言に載る）。
   - 作ったセッションは test の終わりに BFF のログアウトで失効させる。

## 却下した案

- **realm の宣言に 7 人目の試験アカウント**: 常設の PoC にも作られ、資格情報の供給経路と門の数が変わる（コンテキスト）。
- **BFF に利用者作成の口を足す**: 試験のためだけに製品の管理面を広げる。
- **管理 API をエッジで開ける**: 管理面の拒否（NFR-18）を弱める。
- **Keycloak の Pod の中で kcadm.sh を exec する**: 別の JVM が Keycloak 本体を OOMKilled にする（`reconcile-realm.sh` の注記）。
- **使い捨ての Pod（Job）で作る**: 試験の中から作成・確認・削除を順に呼ぶには往復が多い。port-forward なら Playwright の要求の文脈で書ける。

## 残余リスク

- port-forward は試験のプロセスが kubectl を起こす。kubectl・context が無いランナーでは beforeAll が例外で止まる（使い捨てのスタックの外では、門がもっと前で止める）。
- リカバリーコードの未達（決定 4）は残る。追跡先は #2372。
