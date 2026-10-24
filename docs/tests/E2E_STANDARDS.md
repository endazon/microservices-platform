---
title: E2E テスト基準（Playwright。実行層・命名・ロケータ・認証・試験データ・a11y・成果物・品質ゲート・CI）
type: test-spec
status: in-progress
created: 2026-10-21
updated: 2026-10-24
author: Claude
---
<!-- trace:
ids: [NFR, NFR-30]
adrs: [ADR-0031, ADR-0032, ADR-0090]
iadrs: [IADR-0624, IADR-0623, IADR-0622, IADR-0621, IADR-0619, IADR-0614, IADR-0569, IADR-0330, IADR-0337, IADR-0440, IADR-0442, IADR-0248, IADR-0125, IADR-0294, IADR-0255]
specs: [20261024_2025_live-playwright-config, 20261024_2037_e2e-flaky-tally, 20261024_2038_eslint-plugin-playwright, 20261024_2036_e2e-shards-and-artifacts, 20261021_2014_e2e-standards-and-layers, 20261024_2015_axe-all-screens, 20261024_2191_keycloak-theme-axe, 20261024_2023_i18n-en-us-e2e]
issues: [#2013, #2014, #2015, #2191, #2023, #2036, #2038, #2037, #2025]
-->

# E2E テスト基準

> リポジトリ単位の横断ドキュメント。Playwright によるブラウザ E2E の書き方と流し方の基準を置く。
> 層・タグ・Playwright の project 名・opt-in の環境変数名は、E2E の実行層を定めた実装 ADR が決定の正本であり、
> 本書の表はその写しである。テスト戦略全体（種別と責務・ゲート）は [`TEST_STRATEGY.md`](TEST_STRATEGY.md) を見る。
>
> 本書は**基準**であり、ここに書いた配線（live 用 config・ワークフロー・検査器）の多くは**未配備**である。
> 各節で「現状」と「目標」を分けて書く。現状を書いていない規約は、既に守られているか、
> 新しく書く spec から適用するものである。

## 目次

- [1. 命名](#1-命名)
- [2. 実行の層](#2-実行の層)
- [3. ロケータ](#3-ロケータ)
- [4. 待機と時間](#4-待機と時間)
- [5. 認証と試験アカウント](#5-認証と試験アカウント)
- [6. 試験データ](#6-試験データ)
- [7. アクセシビリティ](#7-アクセシビリティ)
- [8. 視覚回帰](#8-視覚回帰)
- [9. 失敗時の成果物](#9-失敗時の成果物)
- [10. flaky の扱いと品質ゲート](#10-flaky-の扱いと品質ゲート)
- [11. CI の配線](#11-ci-の配線)
- [12. 現状との差分](#12-現状との差分)

用語: 「層」はテストの実行層（§2）。「スタブ」は `page.route` によるネットワーク層の差し替え。
「live」は稼働中の PoC（単一の k3s クラスタ）に対して流すこと。

## 1. 命名

| 対象 | 規約 | 例 |
| --- | --- | --- |
| spec ファイル（L1） | `sc<NN>-<topic>.smoke.spec.ts`。画面ルートの母集合検査（`scripts/check-route-manifest.js`）がこの形を数える | `sc19-private-notes.smoke.spec.ts` |
| spec ファイル（live 層） | `e2e/live/<area>.<layer>.spec.ts`。`<layer>` ∈ `ro` / `rw` / `trading` / `llm` | `e2e/live/knowledge.ro.spec.ts` |
| setup ファイル（live 層） | `e2e/live/*.setup.ts` | `e2e/live/auth.setup.ts` |
| 横断 spec | `<topic>.smoke.spec.ts`（a11y / keyboard-navigation / bundle-splitting / session-revocation が既存） | `shell.smoke.spec.ts` |
| describe | 画面名かユースケース名を日本語で書く。先頭に起点 ID を書かない（ID はテスト直前のコメントへ。`scripts/check-test-traceability.js` が読む） | `test.describe('個人資料の共有', …)` |
| test タイトル | 「〜すると〜になる」の 1 文。否定系は「〜しない」「〜を呼ばない」で終える | `'ロール無しで開くと見つかりませんでしたを返し、GET /documents を呼ばない'` |
| タグ | 優先度 `@p0` `@p1` `@p2`、層 `@live-ro` `@live-rw` `@trading` `@llm`、観点 `@a11y` `@visual`。**L1 は層タグを付けない**（層タグが無いこと＝L1） | `test('…', { tag: ['@p0', '@live-ro'] }, …)` |
| 試験データの識別子 | `e2e-<runId>-<短い語>`。`runId` = `<YYYYMMDD>-<GITHUB_RUN_ID または local>-<sha7>` | `e2e-20261021-1234567-abc1234-note` |
| 理由欄（理由の入力が必須の操作） | `[e2e <runId>] <目的>`。復元するときは `[e2e <runId> restore]` | — |
| 試験アカウント | `e2e-<role>`（§5） | `e2e-admin` |

## 2. 実行の層

| 層 | 名称 | 対象 | 起動 | 前提 | 書き込み |
| --- | --- | --- | --- | --- | --- |
| **L1** | CI スタブ層 | ビルド済み SPA（`vite preview`）＋ `page.route('**/bff/**')` | PR 毎・push 毎 | 基盤を 1 つも起動しない | なし（スタブ） |
| **L2** | live 読み取り層（`@live-ro`） | 稼働中 PoC のエッジ。実 Keycloak ログイン（TOTP）→ 実 BFF → 実サービス | nightly ／手動 | 試験アカウント・seed | **禁止**。GET のみでフォーム送信をしない。ログインの副作用（セッションの作成）だけを許す |
| **L3** | live 書き込み層（`@live-rw`） | 同上 | 手動 ／週次 | L2 ＋ 後始末の経路 | 自分が作ったものだけ。識別子必須・afterEach で削除または復元 |
| **L3-T** | 取引影響層（`@trading`） | 同上（取引機能を持つ可変ユニットだけが使う。本リポジトリの platform・knowledge には該当する spec が無い） | **閉場後にのみ手動** | L3 ＋ 市場閉場の機械判定 ＋ オーナーの明示開始 | 設定の変更と復元。実弾（REAL）への切替は送らない |
| **L3-L** | LLM 費用層（`@llm`） | 同上 | 手動 ／週 1 回まで | L2 ＋ 1 実行あたりの LLM 呼び出し回数の上限 | LLM 呼び出しを伴う読み取り操作 |

### 2.1 層の分け方（project ＋ タグ ＋ opt-in）

層は **Playwright の project とタグの両方**で分ける。project はファイル名の接尾辞で、タグは spec の中の宣言で選ぶため、**層タグと接尾辞を一致させる**（`*.ro.spec.ts` ⇔ `@live-ro` など）。

| config ファイル | project | testMatch | storageState | workers | retries | trace |
| --- | --- | --- | --- | --- | --- | --- |
| `playwright.config.ts` | `smoke`（L1） | `e2e/**/*.spec.ts`（`e2e/live/**` を除く） | 無し（`/bff/auth/me` をスタブ） | 既定 | CI で 1 | `on-first-retry` |
| `playwright.live.config.ts` | `live-setup` | `e2e/live/*.setup.ts` | —（作る側） | 1 | 0 | **`off`**（パスワードとセッション cookie を記録させない。§9） |
| 同上 | `live-ro`（L2） | `e2e/live/**/*.ro.spec.ts` | 役割ごと（`test.use`） | 2 | 1 | `retain-on-failure` |
| 同上 | `live-rw`（L3） | `e2e/live/**/*.rw.spec.ts` | 同上 | **1** | 1 | `retain-on-failure` |
| 同上 | `trading`（L3-T） | `e2e/live/**/*.trading.spec.ts` | 取引オーナー役 | **1** | 0 | `on` |
| 同上 | `llm`（L3-L） | `e2e/live/**/*.llm.spec.ts` | `e2e-user` | 1 | 0 | `retain-on-failure` |

- **opt-in**: live 用 config は `E2E_LIVE` が未設定・空なら全 project を 0 件にする。`1` だけが live で、それ以外の値は読み込みで失敗させる。`E2E_LAYER` は
  `ro` / `rw` / `trading` / `llm` のカンマ区切りで、未設定なら `ro` とみなす。`live-setup` は
  選ばれた層の依存として常に走る。値域外の値、または `E2E_LIVE=1` 無しの `E2E_LAYER` は
  **config の読み込みで失敗させる**（綴り違いが黙って 0 件の緑になるのを防ぐ）。
- **現状**: ~~L1 の project 名は `chromium` である。次に `playwright.config.ts` を触る変更で `smoke` へ改める。~~
  **［2026-10-24］L1 の project 名を `smoke` へ改めた。** あわせて en-US の project `smoke-en-us` を足した
  （`locale: 'en-US'`。`e2e/i18n.smoke.spec.ts` だけを走らせ、`smoke` はこの spec を走らせない）。
  **［2026-10-24］`playwright.live.config.ts` と `e2e/live/`（`auth.setup.ts`・`support/`）を置いた**。`E2E_LIVE=1` が無ければ 0 件で終わり
  （`pnpm --filter @platform/frontend run test:e2e:live`）、前提（資格情報 12 個・Keycloak の discovery 200・BFF の `/bff/auth/me` 401）が
  欠ければ exit 2 で終わる。L1 の config は `e2e/live/` を `testIgnore` する。live の spec（`*.ro.spec.ts` など）はまだ無い。
- **PR で必ず流すのは L1 だけ**。L2 以降は PR に掛けない（稼働環境に依存する赤で PR を止めない。
  統合スタックの nightly を必須にしなかったのと同じ理由）。
- **［2026-10-24 訂正］** 従前ここは「PR の必須チェックは L1 だけ」と書いていたが、L1 の check `e2e` は
  ブランチ保護の必須チェックに**入っていない**。`frontend.yml` は `paths:` を持ち、フロントを触らない PR では
  起動しないため、必須にすると恒久 pending になる（必須チェックの一覧は [`../ai-workflow.md`](../ai-workflow.md) の表が正本）。
  L1 はフロントを触る PR では必ず走り、赤ければレビューとマージの判断で止める。

### 2.2 どの層に置くか

- 下位の層（Vitest・契約フィクスチャ・.NET の統合テスト）で守れるものは L1 に置かない。
- L1 は「画面が契約どおりに描き、呼ぶべきものを呼び、呼ばないものを呼ばない」まで。
- L2 は「本物の認証・経路・権限が通る」ことだけ。
- L3 は「書いたものが読める・消せる」まで。境界値・バリデーションの枝は下位の層へ置く。
- UI の無い経路は API で検証する（ブラウザ E2E にしない）。

## 3. ロケータ

- 優先順は `getByRole(name)` → `getByLabel` → `getByText(exact)` → `getByTestId`。`getByTestId` は最後の手段で、
  使うときは理由をコメントに書く（実測: `getByRole` 249 件・`getByTestId` 7 件。この順序は現状の延長である）。
- `name` は **ja の表示文言**で書く（`locale` は `ja-JP` 固定）。文言は Lingui カタログの正本から引き、英語を書かない。
  **`exact: true` を既定**にし、同名の衝突はフォームやリージョンで絞る。
- 存在秘匿の NotFound は `getByRole('heading', { name: '見つかりませんでした' })` で引く。
- `data-testid` を新設するときは kebab-case の意味のある名前（`issued-secret`）にする。動的な id を含めるなら
  `${prefix}-${id}` の形にする。
- CSS セレクタ・XPath・`nth()` を使わない。**［2026-10-24］`nth()` / `first()` / `last()` は ESLint の `playwright/no-nth-methods`（error）が止める**。
  CSS セレクタ・XPath は規約だけである（Keycloak のテーマの画面は Keycloak の markup を id で引くため、機械では止めていない）。
- 色・位置・アイコンの描画に依存しない（「色だけで意味を持たせない」と同じ側に立つ）。

## 4. 待機と時間

- 待機は **web-first アサーションに限る**。`waitForTimeout` を使わない（**［2026-10-24］ESLint の
  `playwright/no-wait-for-timeout`（error）が止める**。`expect(await locator.allTextContents())` のような
  web-first でない比較も `playwright/prefer-web-first-assertions` が止める）。`waitForResponse` はスタブ層で「呼ばれたこと」を見るときだけ使う。
- 「呼ばない」ことの検証は、`e2e/support/bffSession.ts` の `expectBffTrafficIsComplete` と未処理リクエストの記録で行う。
  時間で待って 0 件を見るのではなく、**本体の描画完了を待ってから記録を見る**。
- タイムアウト: L1 は test 30 秒（現状）。L2 は 60 秒、L3 系は 120 秒。`expect` の既定 5 秒は変えず、遅い操作だけ
  個別に `timeout` を渡す。`globalTimeout` を層ごとに置く（L1 15 分・live 40 分）。
- 日時に依存するアサーション（履歴の並び・相対時刻）は固定値を比べず、順序だけを見る。

## 5. 認証と試験アカウント

- **SPA はトークンを扱わない**。L1 では `/bff/auth/me` を差し替えて身元を与える（`e2e/support/bffSession.ts` の
  `installBffSession`）。L2 以降では**実ログイン → BFF セッション cookie（`__Host-msp-session`）を storageState に保存**し、
  役割ごとに 1 ファイル（`playwright/.auth/<role>.json`。`.gitignore` に入れる）を作る。
- ログインは **`live-setup` が役割ごとに 1 回**行う。TOTP は `scripts/lib/totp.js` で計算し、secret は環境変数
  `E2E_<ROLE>_TOTP_SECRET`、パスワードは `E2E_<ROLE>_PASSWORD` から読む。**値をリポジトリに書かない**
  （供給は OpenBao → External Secrets。例示設定には変数名だけを書く）。
- 試験アカウントは**役割ごとに専用**にする: `e2e-user` / `e2e-operator` / `e2e-admin` / `e2e-noaccess` / `e2e-revocable`。
  取引機能を持つ可変ユニットは自前の取引オーナー役を足す。人のアカウント（既存 seed の `admin` / `developer` 等）は
  E2E から使わない。
- MFA を試験のために外さない。検証する側が第二要素を出す。
- **ロックアウト対策**: brute force 保護（5 回失敗で 900 秒）があるため、`live-setup` の `retries` は 0 にする。
  失敗したら `test.skip` ではなく fail にし、どの段（ログイン画面・OTP 画面・コールバック）で止まったかを attach する。
- 書き込み層（L3）はサーバー側の状態を変えるため **`workers: 1`** にする（アカウントの並列割当は後続の改善に残す）。
- 未認証のテストは `test.use({ storageState: { cookies: [], origins: [] } })` で明示する。
- アカウントの状態を変えるテスト（セッション失効など）は専用アカウント `e2e-revocable` を使い、afterEach で有効化へ戻す。

## 6. 試験データ

- **L1**: 契約フィクスチャと orval 生成型（`bff.schemas.ts`）から組む。手書きの JSON を増やさない。
- **L2**: seed（`abac-seed` / `search-seed` / `tag-seed`、realm JSON）に依存する。seed の有無は test の先頭で確かめ、
  無ければ `test.skip(true, '<seed 名> 未投入')` で**理由付きの skip** にする。**全 skip は赤にする**
  （門は依存ごとに訊き、全部が skip なら緑にしない。**目標**: `scripts/check-e2e-skips.js` で検査する）。
- **L3**: 作るものには `e2e-<runId>-` 接頭辞を必ず付け、afterEach で削除する。削除 API が無いもの（履歴・監査行）は
  作らないか、残ることを網羅表に明記する。履歴を見るテストは「自分の行が増えた」ことだけを見て、絶対件数を見ない。
- **sweep**: 残骸を掃く `scripts/e2e-live-sweep.js --live --older-than 24h`（接頭辞一致・作成日時。**目標**）を
  nightly の最後に流す。接頭辞が一致しないものは消さない。
- 乱数は seed を `testId` から決め、再現できるようにする。

## 7. アクセシビリティ

- **axe（`@axe-core/playwright`）は L1 で全画面**に走らせる。ダーク・ライトの両テーマで走査する。
  土台は `e2e/support/axe.ts`（fixture `makeAxeBuilder` / `expectAxeClean`）であり、spec はここから `test` を引く。
- **適合レベル**: 計画の非機能要件が **WCAG 2.2 レベル AA** を定めている。機械検査が検出する違反は 0 件に保つ。
  手動の検査は機械検査の代わりにしない。逆に、**機械検査の 0 件は適合の証明ではない**。
- **タグ**: `wcag2a` / `wcag2aa` / `wcag21a` / `wcag21aa` / `wcag22aa` ＋ `best-practice`（ランドマークの規則は
  `best-practice` にしか属さない）。走査のたびに「このタグで走ったこと」を確かめる（タグが黙って欠けると、
  その版で加わった達成基準は永久に緑である）。
- **走査面**（`e2e/a11y.smoke.spec.ts`）: SPA が描く全画面（ログイン画面の SPA 側の入口を含む）＋
  共通シェル＋存在秘匿の 404 ＋ **操作で現れる UI を開いた状態**（確認ダイアログ・共有先の listbox・
  緊急アクセスのダイアログ・編集パネル・タブ・競合の解決欄）。各面は**最も緩い許可ロール**で開く。
  - 🔴 **Keycloak のテーマが描く画面（ログインの Keycloak 側・二要素認証・パスワード再設定・アカウント設定）は L1 の対象外**である。SPA の
    ビルド成果物に含まれず、基盤を 1 つも起動しない層では描けない。
  - それらの画面は、**CI のジョブの中で起こす使い捨ての Keycloak**（配布物と同じイメージにリポジトリのテーマと realm を載せる）で走査する。
    ワークフローは `keycloak-theme-a11y.yml`、config は `playwright.keycloak.config.ts`、spec は `e2e/keycloak/*.kc.ts`
    （L1 の既定の testMatch に当たらない接尾辞）。タグは `e2e/support/axe.ts` から引く。明暗は project の `colorScheme` で与える
    （Keycloak のテーマは OS の明暗の設定だけに従う）。毎回、テーマの `--color-bg` がその明暗の値であることを確かめる（テーマが解決できないと
    Keycloak は既定のテーマへ黙って落ちるため）。既知の違反の台帳は `e2e/keycloak/axeKnownViolations.ts` に分けて持つ（作法は同じ。現在 0 件）。
    走査用の利用者は管理 API で作って消すので、稼働の Keycloak へは向けない。
- **既知の違反**は `e2e/support/axeKnownViolations.ts` の台帳に、`<規則 ID> <対象セレクタ>` の指紋と
  **1 件ずつの理由**で載せる。走査結果とは**一致**を求める（増えても、直して減っても、台帳を直すまで落ちる）。
  `toMatchSnapshot` は採らない（`--update-snapshots` で理由なしに受け入れられるため）。
  画面側を直せる違反は直すのが先であり、台帳は直すと変更が肥大するものに限る。現在の件数は 0 である。
- キーボード経路（skip link・Tab 順・フォーカストラップ・Esc）は代表画面 1 本（現状）＋ モーダルを持つ画面ごとに 1 本。
- live 層では axe を走らせない（描画は L1 と同じ SPA であり、重複になる）。

## 8. 視覚回帰

- **既定では採らない**。スクリーンショット比較は OS・ブラウザ・フォント・テーマ・ロケールで揺れ、flaky の典型である。
- 採る場合の条件: 公式 Docker イメージ（`mcr.microsoft.com/playwright:<pinned>`）で固定し、`@visual` タグの別 project
  `visual` で流す。`maxDiffPixelRatio ≤ 0.01`、`animations: 'disabled'`、揮発要素は `mask` する。
  対象は「見え方そのものが規約」の少数（NotFound・確認ダイアログ・出典ラベルなど）に限り、PR の必須チェックに入れない。

## 9. 失敗時の成果物

- trace: L1 は `on-first-retry`（現状）。live 層は `retain-on-failure`（再試行が無いか 1 回なので初回から残す）。
  **ただし `live-setup` は trace・スクリーンショット・動画を残さない**（trace はログインの入力と応答の `Set-Cookie` を記録する）。
  失敗した段（ログイン画面・OTP 画面・コールバック）は文字で attach する（URL は path だけ）。
- 🔴 **秘密の値（パスワード・OTP）を `locator.fill(value)` で入れない。** `fill` は値を step の題に載せ、html レポートに残す
  （実測: 偽の BFF と Keycloak に対して setup を流したら、6 名分のパスワードがレポートに出た）。`e2e/live/auth.setup.ts` の
  `fillSecret`（値をページの中で入れる。`evaluate` の引数は題に載らない）を使う。
- `screenshot: 'only-on-failure'`、`video: 'off'`（トレースを見る）。
- reporter: CI はシャードごとに `blob`（＋ログ用の `list`）を書き、`merge-reports` で `html` ＋ `github` にまとめる。ローカルは `list`。
  **［2026-10-24］配備した**: L1 は 2 シャード（`frontend.yml` の `e2e-shard` ジョブ）で流し、`e2e` ジョブがまとめる。
- 成果物のアップロードは **`if: ${{ !cancelled() }}`** で常に行い、14 日保存する（**［2026-10-24］配備した**:
  シャードごとの blob `e2e-blob-report-<N>` と、まとめた html `e2e-html-report`。trace は両方に含まれる）。
- live 層では BFF 応答の本文を trace に残さない（`trace: { mode: 'retain-on-failure', snapshots: true, sources: false }`。
  `page.route` で本文をログへ書かない）。cookie を含む storageState を成果物に含めない。
- axe の結果は `testInfo.attach('axe', { body: JSON.stringify(results) })` で添付する。

## 10. flaky の扱いと品質ゲート

- **PR（L1）**: `retries: 1`（現状）。flaky は pass と数えない。**［2026-10-24］配備した**:
  - **集計・可視化**: `frontend.yml` の `e2e` ジョブが、html レポートと同じ実行結果を JSON でも書き、`scripts/check-e2e-flaky.js` が
    flaky（再試行で通った test）を数える。**1 件でも flaky なら test ごとに警告の注釈**を出し、件数を step summary に表で出す。
    flaky で PR は落とさない。レポートを読めない・test が 0 件なら赤にする（0 件の集計を「flaky 0」と誤らない）。
    要約（`e2e-flaky-summary`）を成果物として 14 日残す。
  - **再発時の扱い**: develop への push の実行で、**同じ test が直近 3 回連続で flaky なら、test ごとに専用 issue を自動で起票**する
    （`e2e-flaky-issue` ジョブ。ラベル `e2e-flaky`。在ればコメントを足す。自動ではクローズしない）。PR の実行では起票しない。
    直前の実行に要約が無い（e2e をまとめられなかった等）ときは、そこで履歴を打ち切る（飛ばして古い実行と繋がない）。
- 🔴 **flaky を隔離（`test.skip` / `test.fixme` / 再試行回数の引き上げ / 実行から外す）して緑にしない。** 起票された issue で原因を直す
  （§4 の待ち方・試験データの共有・時刻依存・スタブの抜け）。**［2026-10-24 改定］** 従前ここには「live は 2 nightly 連続で flaky なら
  `test.fixme` で隔離し、2 週間以内に解消するか削除する」と書いていたが、隔離で緑にする規約は採らないことにした（skip・隔離は検知を
  止めるだけで、壊れた test と壊れた画面を区別できなくする）。live 層の集計は、live 層を配備する変更で同じ検査器を使う。
- `forbidOnly`（現状）。`test.skip` には理由を必須にする（**［2026-10-24］ESLint の `playwright/require-annotation-reason` が止める**。
  L1 の `test.skip` は条件つきでも `playwright/no-skipped-test` が止める。条件つきの skip を許すのは `e2e/keycloak/` の `*.kc.ts` と `e2e/live/` だけ）。skip の上限は L1 で 0、live は前提（門）による skip だけを許し、
  全 skip は赤にする（§6）。
- 検査器・規約の追加は同型の事故 2 回からとする（1 回目は記録に留める）。
- 実行時間の予算: L1 は **1 シャードあたり 10 分以内**（超えたらシャードを増やす）。live-ro は 20 分、live-rw は 30 分。

## 11. CI の配線

| 層 | ワークフロー | トリガ | 必須チェック | 成果物 |
| --- | --- | --- | --- | --- |
| L1 | `frontend.yml` の `e2e-shard` ジョブ（2 シャード）と、まとめる `e2e` ジョブ（check 名は `e2e`） | PR・push（フロントの `paths:` に当たるとき） | いいえ（`paths:` を持つため。§2.1） | blob/html report・trace（常時・14 日） |
| （Keycloak のテーマの axe） | `keycloak-theme-a11y.yml` の `keycloak-theme-a11y` ジョブ（現状あり。使い捨ての Keycloak をジョブの中で起こす） | テーマ・realm・compose・axe の土台を変える PR・push、dispatch | いいえ | 失敗時のみ html report・test-results（7 日） |
| L2 | `e2e-live.yml`（**目標**。統合スタックの k3d 上、または稼働中の PoC に対する self-hosted runner） | nightly・dispatch | いいえ | 同上 ＋ skip 一覧 |
| L3 / L3-T / L3-L | 同 `e2e-live.yml` の `workflow_dispatch` 入力 `layer`（`rw` / `trading` / `llm`）を `E2E_LAYER` へ渡す。`trading` は閉場判定を job の先頭に置く | dispatch のみ | いいえ | 同上 ＋ 復元ログ |

- live 層の job の先頭で、対象イメージの作成時刻と develop の HEAD の日付を比べ、7 日より古ければ `::warning::` を出して
  結果に注記する（判定は落とさない。古いイメージに対する結果を新しいコードの結果と取り違えないため）。
- 稼働中の PoC での流し方（試験アカウントの供給・TOTP・到達先・閉場判定と復元・LLM 費用の上限）は別資料にあり、
  live 層を配備する変更で本書の節として取り込む。

## 12. 現状との差分

| 項目 | 現状 | 本書の目標 |
| --- | --- | --- |
| L1 の project 名 | ~~`chromium`~~ `smoke`（2026-10-24 に改めた。ほかに `smoke-en-us`） | `smoke` |
| 成果物のアップロード | ~~なし~~ L1 は `!cancelled()`・14 日（2026-10-24） | `!cancelled()`・14 日 |
| `timeout-minutes` | ~~なし~~ L1 は 20 分（2026-10-24。`globalTimeout` は CI で 15 分） | job に置く（L1 20 分、live 60 分） |
| `fullyParallel` | ~~未指定（ファイル単位）~~ true（2026-10-24） | true（シャードの均等化） |
| L1 のシャード | 2（2026-10-24） | 1 シャードあたり 10 分以内（§10） |
| axe のタグ | `wcag2a` / `wcag2aa` / `wcag21a` / `wcag21aa` / `wcag22aa` / `best-practice`（達成済み） | 同左 |
| axe の走査面 | SPA の全画面 ＋ 操作後の UI ＋ Keycloak のテーマの画面（達成済み。Keycloak のテーマの画面は使い捨ての Keycloak で走査する） | 同左 |
| live のブラウザ E2E | なし（curl の `scripts/verify-oidc-edge-flow.sh` だけ） | L2〜L3 を新設。curl の検証は残す（API 層の門として役割が違う） |
| 試験アカウント | seed の `developer` 等を人と共用 | `e2e-*` を役割ごとに分離 |
| 視覚回帰 | なし | 既定で採らない（候補 1 件） |
| flaky の集計 | ~~なし~~ L1 は件数の集計・警告・3 回連続の自動起票（2026-10-24） | 件数の収集と自動起票（隔離はしない） |
| ESLint（Playwright 用ルール） | ~~なし~~ `eslint-plugin-playwright` の recommended ＋ 下の 3 つを error（2026-10-24。対象は `**/e2e/**`） | `no-wait-for-timeout`・`no-nth-methods`・skip 理由必須 |
