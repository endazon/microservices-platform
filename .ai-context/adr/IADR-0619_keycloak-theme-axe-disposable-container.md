---
title: IADR-0619 Keycloak のテーマの画面（SC-13 の Keycloak 側・SC-14〜16）は、CI のジョブの中で起こす使い捨ての Keycloak で axe を走らせる（静的描画は採らない）。親テンプレートのラベルの欠陥はテーマの追加スクリプトで補う
type: impl-adr
status: Accepted
related_ids: [NFR-30, SC-13, SC-14, SC-15, SC-16, ADR-0026, ADR-0031, IADR-0614, IADR-0532, IADR-0569, IADR-0261]
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（NFR-30）
  - planning:projects/microservices-platform/05_screens/01_screens.md（§SC-13〜16）
related_specs:
  - ../specs/20261024_2191_keycloak-theme-axe.md
---

# IADR-0619: Keycloak のテーマの画面は使い捨ての Keycloak で axe を走らせる

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-24
- 決定者: claude（#2191）

## 起点・関連

- 関連する計画書 ID: NFR-30（WCAG 2.2 レベル AA。機械検査のタグ `wcag2a`・`wcag2aa`・`wcag21a`・`wcag21aa`・`wcag22aa`）、
  SC-13〜16（Keycloak のテーマとして実装）、計画 ADR-0026（認証画面は Keycloak のテーマ）・ADR-0031（フロントの技術）。
- 前提とする実装 ADR: [IADR-0614](IADR-0614_axe-wcag22-aa-all-screens-and-known-violation-ledger.md)（SPA の axe。決定 3 で Keycloak のテーマの画面を L1 の対象外とし、
  残余に挙げた）、[IADR-0532](IADR-0532_keycloak-theme-nocturne-parents-and-token-sync.md)（テーマの親・トークンの生成・テンプレートは複製しない）、
  [IADR-0569](IADR-0569_e2e-layers-and-live-opt-in.md)（E2E の層・`E2E_KC_URL` の名前の予約）。
- 作業仕様書: [20261024_2191_keycloak-theme-axe](../specs/20261024_2191_keycloak-theme-axe.md)。

## コンテキストと課題

- SC-13 の Keycloak 側と SC-14〜16 は、親テーマ（login = `keycloak.v2`、account = `keycloak.v3`）のテンプレートと React アプリを Keycloak が描き、
  本リポジトリは CSS・メッセージ・ロゴだけを持つ（IADR-0532 決定 1）。SPA のビルド成果物には含まれず、L1 では描けない。
- MSP#2183 でライトの `--color-warning` を変えた。このトークンは Keycloak のテーマにも効くが、テーマの画面のコントラストは機械で確かめていなかった。
- 決めること: (a) Keycloak を起動せずに走査できるか、できないならどこで起動するか、(b) 走査する面、(c) 違反の直し方。

## 検討した選択肢

| # | 案 | 評価 |
| --- | --- | --- |
| A | テーマのテンプレートを**静的に描画**して走査する | **成立しない。** 本リポジトリは `.ftl` を持たない（親のテンプレートは Keycloak の配布物の jar の中にある）。描くには FreeMarker と Keycloak の描画モデル（`url`・`realm`・`msg`・`messagesPerField`・`totp` 等）を自前で模す必要があり、模した値が実物と食い違っても気付けない。アカウントコンソール（SC-16）は React アプリで、account の REST API から描くため、テンプレートの描画では現れない |
| B | 稼働中の Keycloak から**描いた HTML を採取してコミット**し、CI はそれを走査する | 起動は不要になるが、親の CSS（PatternFly 5）と採取した DOM をコミットし続けることになり、Keycloak の版上げのたびに採取し直しが要る。採取を忘れると古い DOM に今の CSS を当てて測り続ける（緑のまま実物とずれる） |
| C | 既存の **integration-stack**（k3d に全基盤。nightly・約 45 分）に axe を足す | 起動は済んでいるが、テーマを変える PR では走らない（nightly・develop の push だけ）。全基盤の起動を待つ価値が本検査には無い |
| D | **CI のジョブの中で使い捨ての Keycloak を 1 つ起こし**（配布物と同じイメージ・リポジトリのテーマと realm）、Playwright ＋ axe で歩く（採用） | 親のテンプレート・React アプリ・テーマの CSS をすべて実物で描く。テーマ・realm・axe の土台を変える PR で走る |

Keycloak を起動する方式（C・D）しか成立しないため、D の費用を見積もった（§結果）。

## 決定

### 決定 1: 走査は専用のワークフロー `keycloak-theme-a11y.yml` で、ジョブの中に起こした使い捨ての Keycloak に対して行う

- イメージは `deploy/docker-compose.yml` の keycloak サービスの `image:`（タグ＋digest）を**そのまま読む**。版を複写しない（compose と k8s の
  タグの一致は `check-realm-constraints.js` 検査 4 が守る）。`start-dev --import-realm` に realm の JSON とテーマのディレクトリを読み取り専用でマウントする。
- Playwright の config は 2 本目の `playwright.keycloak.config.ts`。spec は `e2e/keycloak/*.kc.ts`（L1 の既定の testMatch に当たらない接尾辞。
  L1 の config は変えない）。接続先 `E2E_KC_URL`（IADR-0569 決定 4 が予約した名前）が無ければ config の読み込みで落ちる。
  管理者は `E2E_KC_ADMIN_USER` / `E2E_KC_ADMIN_PASSWORD`（本 IADR で名前を足す。パスワードはジョブの中で乱数から作り、ログでは伏せる）。
- 走査用の利用者は管理 API で test ごとに作り、test の終わりに消す。**稼働の Keycloak へは向けない**（作って消すのは L2 の「書き込み禁止」に当たる）。
- **必須チェックにしない。** paths で起動が絞られ（テーマ・realm・compose・axe の土台・本ワークフロー）、起動しない PR で必須チェックが恒久 pending になる。
  Keycloak の起動の失敗で無関係な PR を止めない点は IADR-0569 決定 1 の L2 以降と同じである。
- 層の名前（IADR-0569 決定 1）は足さない。本検査は稼働環境に依存せず（L2〜L3 ではない）、基盤を 1 つ起こす（L1 でもない）が、
  利用者から見て意味のある区別ではなく、ワークフロー名で足りる。

### 決定 2: 面は計画の各画面の主要素が描かれる状態を 1 つずつ選ぶ（13 面 × 2 テーマ）

- **面の数は `expectKeycloakAxeClean(page, '<面>', …)` の呼び出しの数と一致させる**（1 呼び出し＝1 面。現在 13）。両テーマは呼び出しを
  増やさず、2 つの project が同じ呼び出しを 1 回ずつ通す（走査は計 26 回）。台帳の整合の test は呼び出しの数を閾値に持つ（面を足したら上げる）。

- SC-13: ログイン・認証エラー（固定文言）。SC-14: 初回登録（QR コード／手入力キー）・ワンタイムコード・コードの誤り。
  SC-15: 申請・申請後（「メールを送信しました」）・新パスワードの設定。SC-16: 個人情報・サインイン（パスワード・OTP デバイス）・デバイスの活動（セッション）。
  共通: Keycloak のエラー画面（不正なクライアント）。
- リセットメール（email テーマ）は走査しない。ブラウザの画面ではなく、外枠の色はライトの意味トークンの値に限られている（IADR-0532 決定 2）。

### 決定 3: タグ・テーマ・台帳の作法は IADR-0614 と揃え、台帳は分ける

- タグは `e2e/support/axe.ts` の `WCAG_22_AA_TAGS` を**輸入して**使う（定義を 2 か所に置かない）。毎回 `runOnly` と合格判定 1 件以上を確かめる。
- 明暗は Playwright の project（`colorScheme`）で与える。Keycloak のテーマは `prefers-color-scheme` だけで明暗が決まる（IADR-0532 決定 3）ため、
  SPA の `data-theme` の書き換えは使えない。台帳のキーのテーマは走査時にページの `prefers-color-scheme` から読む。
- 陽性対照に「platform テーマのその明暗で描かれた」ことを足す（`tokens.css` の `--color-bg` が `:root` に効いていること）。テーマが解決できないと
  Keycloak は既定のテーマへ黙って落ち、既定のテーマもおおむね axe を通るため、違反 0 件だけでは自分たちの CSS を測ったことにならない。
- 既知の違反の台帳は `e2e/keycloak/axeKnownViolations.ts`（形・一致・理由の必須は SPA の台帳と同じ）。SPA の台帳と分けるのは、SPA 側の
  「どの走査にも当たらないキー」の検査が Keycloak の面のキーを死に行と判定するためである。使わなくなったキーの検査は Keycloak の spec が持つ。

### 決定 4: 親テンプレートのラベルの対応づけの欠陥は、テーマの追加スクリプトで補う（テンプレートは複製しない）

- 初回の走査で `label` の違反が 3 面 × 2 テーマに出た。いずれも親（keycloak.v2・26.7.4）の欠陥で、`<label for>` の指す id が入力欄と違う:
  - `login-config-totp.ftl`（SC-14 初回登録・手入力キー）: ワンタイムコードとデバイス名のラベルが、どちらも実在しない `form-vertical-name` を指す。
  - `template.ftl` の username マクロ（SC-14 ワンタイムコード・コードの誤り）: ラベルは `username` を指すが、読み取り専用の入力欄は `kc-attempted-username`。
- CSS とメッセージでは対応づけを作れない。テンプレートの複製は IADR-0532 決定 1 が禁じる（本体の修正・新フローへ追随できなくなる）。
  そこで login テーマに `scripts=js/platform-a11y.js` を足し、**`for` の指す要素が実在しないラベルに限り**、同じフォームグループの中に名前の付く入力欄が
  ちょうど 1 つあればそこへ付け替える。正しく対応づいたラベルには触れないので、親が直れば何もしなくなる。
- **補修が黙って何もしない状態にしない。** 壊れたラベルを見つけたのに付け替え先を決められない（`.pf-v5-c-form__group` が無い・入力欄が 1 つでない）ときは
  `[platform-a11y]` で始まる `console.error` を出し、走査の spec の自動 fixture（`labelRepairGuard`）がそれを拾って落ちる。spec は補修後のラベルが
  入力欄を指すことも明示的に確かめる。PF5 のクラスが版上げで変わると、axe の `label` 違反に加えて原因そのものが出る。
- これは IADR-0532 決定 1 の「持つのは CSS・メッセージ・ロゴ・メールの外枠だけ」に**スクリプト 1 つを足す**変更である（複製しない方針は変えない）。
  親（keycloak.v2）は `scripts` を持たないので、置換で失うものは無い。外部への通信は無く、ファイルで読む（Keycloak の CSP の `script-src 'self'` の内側）。
- 台帳へは載せない。直せない理由が無い（直せた）ため。

## 理由

- 静的描画（A）は「親のテンプレートを持たない」という IADR-0532 の設計と正面からぶつかる。持たないものを描くには模すしかなく、模したものを測っても
  利用者が見る画面の適合は言えない。採取（B）は版上げのたびに古くなる記録を増やす。
- 使い捨ての Keycloak（D）は、配布物と同じイメージ・同じテーマ・同じ realm で描くため、版上げ（イメージの digest の変更）もトークンの変更も
  そのまま走査に届く。

## 結果

- 所要時間とディスクの見積り（手元で Keycloak 26.7.4 の配布物を起動して実測。CI の数字は見積り）:
  - Keycloak の起動: augmentation 約 28 秒 ＋ 起動 約 32 秒（realm の取り込みを含む）。CI ではイメージの取得（圧縮で約 200 MB）と合わせて
    1〜1.5 分と見積もり、pnpm install・ブラウザの導入と並行に進める。
  - 走査: 13 面 × 2 テーマで約 35 秒（手元）。ジョブ全体は 4〜6 分と見積もった（`timeout-minutes: 20`）。PR の CI の実測は走査 約 31 秒・ジョブ全体 約 1 分 45 秒
    （Keycloak は後続の手順の間に起動を終え、readiness の待ちは 0 秒だった）。
  - ディスク: GitHub のランナー上でイメージ約 0.5 GB ＋ node_modules ＋ ブラウザ。リポジトリ・稼働クラスタのディスクには影響しない。
- 全 13 面 × 2 テーマで違反 0 件（台帳 0 件）。ライトの `--color-warning`（MSP#2183）を含むテーマの色のコントラストも、この走査が確かめる。
- 変異で赤になることを確かめた（作業仕様書 §検証）: スクリプトを外すと `label` が 4 面 × 2 テーマで、ライトの `--color-fg-muted` を薄くすると
  `color-contrast` がライトの全面で出る。
- 悪い影響・トレードオフ:
  - PF5・親テンプレートの id とクラス名（`#kc-form-login`・`.pf-v5-c-form__group` 等）に依存する。Keycloak の版上げで変われば spec が落ちる（気付ける形で壊れる）。
  - ラベルを補うスクリプトは JavaScript が動かない環境では効かない（親の login テーマは他の部品でも JavaScript を前提にしている）。
  - k8s ローカルの ConfigMap の `items` に 1 行足した（IADR-0532 §結果の注記どおり）。稼働の Keycloak へ届けるには再配備が要る。
- 残余: 親の欠陥（ラベルの id）を Keycloak 本体へ報告していない。報告して直れば、本スクリプトは何もしなくなる（消してよい）。
