---
title: IADR-0625 live E2E は使い捨ての統合スタックの上で、ジョブの中で作った試験アカウントで流し、成果物は資格情報を走査してから 14 日残す
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-18, ADR-0090, IADR-0569, IADR-0570, IADR-0624, IADR-0248]
author: claude
created: 2026-10-24
updated: 2026-10-25
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0090（門は依存ごと。全 skip を緑にしない）
related_specs:
  - ../specs/20261024_2026_e2e-live-workflow.md
---

# IADR-0625: live E2E のワークフロー（#2026）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-24
- 決定者: claude（#2026。親 #2013 の E-32）

## 起点・関連

- 起点 issue: #2026（親 #2013）。関連する計画書 ID: NFR、NFR-18（秘密の扱い）。計画 ADR: ADR-0090。
- 関連する実装判断: [IADR-0569](./IADR-0569_e2e-layers-and-live-opt-in.md)（層と opt-in）・[IADR-0570](./IADR-0570_e2e-accounts-placeholder-credentials.md)（試験アカウント）・
  [IADR-0624](./IADR-0624_live-playwright-config-setup-and-readiness.md)（config・setup・teardown）・[IADR-0248](./IADR-0248_integration-stack-ci-readiness-gate.md)（稼働環境に依存する検査を必須にしない）。
- 作業仕様書: [20261024_2026_e2e-live-workflow](../specs/20261024_2026_e2e-live-workflow.md)。

## コンテキストと課題

live の config（IADR-0624）を流す CI が無い。流す先は 2 つ考えられる: 使い捨ての統合スタック（GitHub-hosted の k3d）と、常設の PoC（self-hosted runner）。
live は実の Keycloak にパスワードと OTP を入れ、BFF のセッション cookie を受け取り、成果物（trace）に cookie が載る。秘密の置き場と成果物の扱いを決める必要がある。

## 検討した選択肢

| 論点 | 案 | 採否 |
| --- | --- | --- |
| 流す先 | A. 常設の PoC（self-hosted runner） | 本 PR では採らない。runner が未配備で、資格情報を GitHub の secret に置く必要がある |
| | **B. 使い捨ての統合スタック（integration-stack と同じ起動器）** | **採る** |
| 試験アカウントの値 | A. GitHub の secret に置く | 採らない。fork の PR では渡らず、渡せば漏れうる。使い捨てのスタックには要らない |
| | **B. ジョブの中で乱数から作り、伏せ、スタックごと消す** | **採る**。リポジトリの secret を 1 つも使わない |
| 成果物の秘密 | A. 上げてから気付く | 採らない |
| | **B. 上げる前に資格情報の値を zip の中まで走査し、載っていれば上げない** | **採る**（`check-e2e-artifact-secrets.js`） |
| 保存期間 | A. セッションの寿命（30 日）より短くする必要がある | 使い捨てのスタックでは当たらない（下の決定 4） |
| | **B. 基準書 §11 の 14 日** | **採る** |
| PR での実行 | A. 走らせない | 採らない。live の土台を壊す変更がマージまで分からない |
| | **B. live の土台を変える PR だけ `paths:` で走らせる（必須にしない）** | **採る**。秘密を使わないので fork でも安全 |

## 決定

1. `.github/workflows/e2e-live.yml`（ジョブ `e2e-live`）: 日次（層 ro）・dispatch（`layer`）・live の土台を変える PR。integration-stack と同じ起動器でスタックを起こし、
   `check-stack-ready.js --live` を門にし、`gen-e2e-accounts.js` の値で Secret `platform-infra/e2e-accounts` を作って `reconcile-realm.sh` で 6 名を足し、
   ルート CA を Node と Chromium に信頼させて、`playwright.live.config.ts` を E2E_LIVE=1 で流す。最後にスタックを消す。
2. `scripts/check-e2e-skips.js` で全 skip（test を持つ project の全部が skip、skip でない test が 0 件）を赤にする。選んだ層に spec が無いのは警告。
3. `scripts/check-e2e-artifact-secrets.js` で成果物の資格情報の値（と TOTP の秘密の base32・各値の base64・Basic 認証の base64）を、zip の入れ子 4 段まで
   走査し（超えたら例外）、0 件のときだけ html レポートと test-results を 14 日上げる。試験アカウントを作る前に落ちた実行では走査も upload もしない。
4. **セッション cookie**: 層の trace に載る（外せない）。teardown が失効させ、失効できなければジョブが赤（Playwright の実行の失敗を握り潰さない）。
   スタックはジョブの終わりに消えるので、30 日のセッションの寿命が 14 日の保存より長くても cookie の使い道は残らない。
5. 権限はワークフロー単位で `contents: read`。起票は PR で走らない `report-failure` だけが `issues: write`・`actions: read` を持つ。必須チェックにしない。

### 初回の実行で見つけた原因と直し方（PR #2226 の `e2e-live`）

- **症状**: スタック・試験アカウントの作成・CA の信頼は通り、setup の 6 役割すべてが「コールバック」段で 60 秒待って落ちた。
  失敗した段の attach の path は 6 件とも `/realms/platform/login-actions/required-action`（パスワードと OTP は通り、Keycloak の必須アクションの画面に残っていた）。
- **原因**: realm の `requiredActions` で `CONFIGURE_TOTP` が `defaultAction: true`（新しい利用者すべてに付く）。試験アカウントの宣言（IADR-0570）は
  `requiredActions` を持たないので、`reconcile-realm.js` が Admin REST API で作ると既定の `CONFIGURE_TOTP` が付き、宣言の OTP の資格情報を持ったまま
  「OTP の登録」へ送られた。（2）OTP のポリシーの不一致・（3）エッジのオリジンの差・（4）スタックの不安定ではない —— OTP を通って Keycloak の別の画面へ進んでいる。
- **直し方**: 6 名の宣言に `"requiredActions": []` を明示した（作成の本文に載り、Keycloak は既定の必須アクションを外す）。
  `scripts.repo.test.js` が「OTP を宣言する利用者は requiredActions を明示し CONFIGURE_TOTP を持たない」を固定する。
  あわせて setup は、必須アクションの画面へ送られたら待ち続けずに「Keycloak が必須アクションを求めている（フォーム: …）」と言って落ちる（L1 の偽スタックで固定）。
- **常設の PoC**: E-30 の手順で既に作った試験アカウントには `CONFIGURE_TOTP` が付いている（後追いは既存の利用者を触らない）。PR #2226 本文の手順で外す。

［2026-10-25 追記 / #2026］**上の「直し方」の括弧書き（作成の本文に載れば Keycloak は既定の必須アクションを外す）は誤りだった。**
3 回目の実行（head `5a16bced`）でも 6 名とも同じ画面（フォーム `kc-totp-settings-form`）に残った。
Keycloak 26.7.4 の `UsersResource.createUser` は `profile.create()`（realm の defaultAction を付けて作る）の後に
`UserResource.updateUserFromRep(…, isUpdateExistingUser=false)` を呼び、その中の `removeMissingRequiredActions = isUpdateExistingUser` により、
**作成（POST /users）では宣言に無い action を外さない**（宣言の action を足すだけ）。外すのは更新（PUT /users/{id}）だけである。
realm の取り込み（`--import-realm`）は既定の action を付けずに作るので、この差は `reconcile-realm.js` の Job で作った利用者にだけ出る。
**直し方**: `reconcile-realm.js` の `user.create` は、宣言が `requiredActions` を配列で持つとき、作成の後の手順（postSteps）に
PUT `/users/{id}`（宣言から資格情報・グループ・ロールを除いた本文）を足して当て直す。`scripts/keycloak-realm-reconcile.test.js` が
6 名の作成にちょうど 1 つの PUT（`requiredActions: []`・資格情報を載せない）が付くことと、宣言に無い利用者には付かないことを固定する（PUT を外すと赤）。
2 回目の実行（head `10a9932b`）は「ログイン画面」段で止まり、ブラウザは `/bff/auth/login` に留まった（readiness の門は BFF 401・discovery 200 を通していた）。
当時は BFF の応答の状態を残していなかったので原因は確定していない。3 回目は診断を足した状態で同じ段を 6 名とも通った。再発したら attach の
`bff /bff/auth/login: <状態> → Location …` と失敗時の BFF のログで切り分ける。

## 理由

- 秘密を持たない CI は、漏れる秘密も持たない。使い捨てのスタックはそれを可能にする。
- 道具が値を残す経路（`fill` の題・trace・error-context のページの写し）は書き手が気を付けても増える。上げる前の機械の走査が最後の門になる。

## 影響

- 日次の実行の時間（スタックの起動 〜30 分 ＋ E2E）が runner に乗る。失敗は issue になる（`ci-failure-issue.yml`）。
- live の土台を変える PR で 1 時間弱のジョブが走る。

## 残余リスク

- **常設の PoC へ流す経路を足すとき、決定 4 の前提（ジョブの終わりで cookie の発行元が消える）が崩れる。** そのときは保存期間をセッションの寿命と
  突き合わせ直す（teardown の失敗時に成果物を上げない、または保存を短くする）。資格情報も GitHub の secret か runner のクラスタの Secret から取る形になる。
- teardown が走らない異常終了（`timeout-minutes` の打ち切り等）でも、スタックの削除（`if: always()`）で cookie の発行元は消える。runner ごと落ちたときは
  スタックも runner と共に消える。
- 選んだ層に spec がまだ無い間（E-40 以降が入るまで）、日次の実行は setup と teardown だけを確かめる（警告が出る）。
- ［2026-10-25 追記 / #2026］**本番の realm の後追い（`reconcile-realm.js`。常設の PoC の Keycloak も同じ経路）の挙動が 2 点変わる。**
  (1) **新しく作る利用者**のうち宣言が `requiredActions` を配列で持つものに、作成の後に PUT `/users/{id}` で宣言の `requiredActions` を当て直す
  （ログに `applied user.update <名前>` が出る）。既存の利用者は作成の経路を通らず、作成が 409（前から在った）でも当て直さないので変わらない。
  宣言が `requiredActions` を持たない利用者には当てない。PUT の本文は資格情報・グループ・ロール・`federatedIdentities` を載せない。
  対象は MSP の realm の人の seed（`admin`・`poc-user`・`poc-operator`・`developer`。宣言は `["CONFIGURE_TOTP"]`＝作成の結果と同じ）と
  e2e-* 6 名（`[]`）、AST の realm の `dev-owner`・`developer`（`[]`。AST の realm に既定の action は無いので結果は同じ）。サービスアカウントは別の経路で、対象外。
  (2) **当てられなかった操作が 1 件でもあれば Job を失敗にする**（従来は ERROR の行を出しても、再計画が差分 0 なら緑だった。作成の後の手順の失敗は差分に戻らない）。
  `k8s-local-up.sh` は Job の失敗で止まるので、従来は黙って通っていた失敗（作成の後のロール割当の失敗を含む）で起動が止まり得る。
