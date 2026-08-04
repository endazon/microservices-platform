---
title: SPA 移行 第 2 段の残り — shadcn/ui 派生プリミティブの本移植・Lingui(ja/en)・Storybook
type: spec
status: done
related_ids: [NFR, ADR-0031, SC-01, SC-02, SC-05, SC-06, SC-07, SC-08, SC-09, SC-10, SC-11, IADR-0034, IADR-0116, IADR-0118, IADR-0119, IADR-0120, IADR-0121, IADR-0124, IADR-0125]
author: Claude
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md"
  - "../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md"
  - "../../planning/projects/microservices-platform/06_technical/08_data-egress-policy.md"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/INDEX.md"
related_specs:
  - ./20260804_issue-490_spa-router-shell.md
  - ./20260804_issue-446_spa-foundation-stack-migration.md
  - ../adr/IADR-0125_ui-primitives-i18n-catalog-and-storybook.md
  - ../adr/IADR-0121_spa-stack-migration-staging.md
  - ../adr/IADR-0124_tanstack-router-unit-composition.md
  - ../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md
---

# 仕様書: SPA 移行 第 2 段の残り（`@platform/ui` の本移植・Lingui・Storybook）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR: 保守性・アクセシビリティ・データ越境統制。全画面の土台）
- 画面（SC）: SC-01〜SC-11（既存 11 画面が使う UI 要素が移植部品の選定根拠）／
  SC-02・SC-05〜SC-09（モックアップの `panel` / `note` / `seg` / `table` / `inp` が同上）
- 関連 ADR（計画）:
  [ADR-0031](../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md)（Accepted。
  **UI = Tailwind CSS v4 + shadcn/ui + Lucide React**・**i18n = Lingui（ja / en）**・
  **コンポーネントカタログ = Storybook** を確定。逸脱不可）
- 関連する技術検討（計画）:
  [13_frontend-stack](../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md)（fixed。
  **§採用技術一覧が正**。i18n 欄の備考「コンパイル時抽出」／§ディレクトリ構成 の `locales/ ja / en（Lingui）`）／
  [08_data-egress-policy](../../planning/projects/microservices-platform/06_technical/08_data-egress-policy.md)
  （**外部 CDN・Web フォント・analytics・エラー報告 SaaS を使わない。全アセットを自己ホストでバンドルする**。
  §非LLM外部送信の統制 は「既定テレメトリをオプトアウトする」も課している＝Storybook の telemetry が該当）／
  [01_screens](../../planning/projects/microservices-platform/05_screens/01_screens.md)（**§画面詳細 と
  `mockups/hi-fi/` が移植部品の選定根拠**）／
  [INDEX](../../planning/projects/microservices-platform/INDEX.md) 決定 21（色だけで意味を持たせない）
- 関連 IADR:
  [IADR-0125](../adr/IADR-0125_ui-primitives-i18n-catalog-and-storybook.md)（**本作業の内部設計判断。本書と対で読む**）／
  [IADR-0121](../adr/IADR-0121_spa-stack-migration-staging.md)（決定 1 = 5 段分割・**決定 4 = `@platform/ui` の切り出し単位**）／
  [IADR-0124](../adr/IADR-0124_tanstack-router-unit-composition.md)（決定 7 = 通知の a11y・第 2 段の分割）／
  [IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md)（**FR-17〜21 の着手保留**。Dialog 見送りの根拠）／
  [IADR-0118](../adr/IADR-0118_backend-coverage-floor.md) / [IADR-0034](../adr/IADR-0034_frontend-coverage-gate.md)（床の ratchet）／
  [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md)（規約 4）／
  [IADR-0120](../adr/IADR-0120_excluded-units-from-gitmodules.md)（AST は別プロジェクト＝本リポから変更できない）
- 本リポジトリの起点: #496（親 #454 / 第 2 段の前半 = #490 = PR #495 / 協調 #452）

## 目的・背景

[IADR-0121](../adr/IADR-0121_spa-stack-migration-staging.md) 決定 1 が定める移行第 2 段は
「TanStack Router 移行・共通シェル・旧 13 画面の削除・**shadcn/ui コンポーネント本移植・Lingui(ja/en)・
Storybook**」である。前 3 者は #490（PR #495）で消化され、**後 3 者が本 issue の範囲**である
（分割の根拠と裁定は [IADR-0121 決定 1 の 2026-08-04 追記](../adr/IADR-0121_spa-stack-migration-staging.md)）。

現状は次のとおりで、いずれも計画の §採用技術一覧と食い違っている。

| 系統 | 現状 | 計画（13_frontend-stack §採用技術一覧） |
| --- | --- | --- |
| UI コンポーネント | `@platform/ui` は `Button` / `StatusBadge` の 2 つのみ | shadcn/ui（共有 UI パッケージとして 2 ユニットで共用） |
| i18n | 無し（日本語リテラルが直書き） | Lingui（ja / en）。コンパイル時抽出 |
| コンポーネントカタログ | 無し | Storybook |

## 対象範囲

### 対象

1. **shadcn/ui 派生プリミティブの `@platform/ui` への本移植**（後述 §1 の選定表に挙げた 8 部品）。
2. **Lingui（ja / en）の導入**——ビルド経路（マクロ）・カタログ（`.po` ＋ コンパイル済み）・
   ロケール活性化・**未翻訳キーの機械検出**。
3. **Storybook のセットアップ**——`@platform/ui` のカタログ化と、**外部 CDN 非依存の機械検査**。
4. 上記に伴うテスト・カバレッジ床の維持（引き下げ禁止。IADR-0118 / IADR-0034 の ratchet）。

### 対象外（送り先を明記する。**繰り延べであって放棄ではない**）

| 事項 | 送り先 | 理由 |
| --- | --- | --- |
| **既存 11 画面（SC-01〜11）の文言の i18n 化** | **#452** | 後述 §2.4。#452 が Page を作り直すため、いま `<Trans>` を入れると**同じ画面を 2 回書く**（[IADR-0121](../adr/IADR-0121_spa-stack-migration-staging.md) 決定 1 が第 2 段の分割で守っている原則そのもの） |
| **Dialog（モーダル）の移植** | **#452**（FR-19 / FR-20 の保留解除後） | 後述 §1。計画がダイアログを要求するのは SC-19 / SC-20 のみで、これは FR-19 / FR-20 に属し [IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md) 決定 1 が着手を保留している |
| 画面へのプリミティブの適用（既存 11 画面の作り直し） | **#452** | #490 仕様書 §親への申し送り の分担どおり |
| ダークテーマのトークン | **#452** | 画面が確定してから決める（`packages/ui/README.md` の既存の申し送り） |
| TanStack Table / ECharts / React Hook Form / Zod / Zustand | 第 4 段（IADR-0121 決定 1） | 画面機能の土台。本 issue は素の DOM プリミティブに留める |
| Knip / Plop / Renovate / Husky / Commitlint / Prettier 実行 | 第 5 段（IADR-0121 決定 1） | 運用系 |
| `oidc-client-ts` の撤去 | 第 3 段（#439） | IADR-0121 決定 6 |
| eslint-plugin-lingui / eslint-plugin-storybook の導入 | **本 issue で導入する**（下記 §3.4） | 13_frontend-stack §採用技術一覧 の Linter 欄が「Storybook / Lingui のプラグインを併用」と明記している |

## 設計

内部設計の判断（選択肢の比較・棄却理由）は
[IADR-0125](../adr/IADR-0125_ui-primitives-i18n-catalog-and-storybook.md) を正とする。本節は実装の形を記す。

### 1. 移植するプリミティブの選定（推測で盛らない）

選定は **(a) 計画が明示的に要求している UI 要素**（`05_screens/01_screens.md` §画面詳細 の主要素・
入力/バリデーション表）と **(b) `mockups/hi-fi/` が実際に描いている要素**、**(c) 既存 11 画面の実装が
実際に使っている DOM 要素**の 3 つを突き合わせて行う。**3 つのいずれにも現れない部品は作らない。**

計測条件: 実装の DOM 要素数は
`grep -rhoE '<(input|select|textarea|table|thead|tbody|th|td|dialog|form|label|button)\b' src/knowledge/frontend/src src/platform/frontend/src --include='*.tsx'`
（`*.test.tsx` を含む全 tsx）を対象コミット `4147899` の worktree で実測した値。
モックアップの語彙は `mockups/hi-fi/sc-*.html`（**画面モックのみ。一覧ページの `index.html` は除く**）の
`class="…"` と要素名を全抽出して数えた値。

| 部品 | (a) 計画の明示 | (b) hi-fi モックの語彙 | (c) 既存実装 | 採否 |
| --- | --- | --- | --- | --- |
| **Input** | SC-01 質問／キーワード（テキスト・必須）／SC-02 検索ボックス／SC-05 タイトル（テキスト・必須） | `inp`（17）・`input` | `<input>` 18 | **移植** |
| **Textarea** | SC-08「分析内容の入力（**テキストエリア**）」 | 同上（複数行入力） | `<textarea>` 3 | **移植** |
| **Select** | SC-01 対象範囲フィルタ（選択）／SC-05 機密区分（**選択**・必須）／SC-09 対象属性（**選択**・必須）／SC-02 検索モード・並び順の切替 | — | `<select>` 9 | **移植** |
| **Label** | 各画面の「入力 / バリデーション」表の項目名 | `olabel`（27）・`<label>`（29） | `<label>` 28 | **移植** |
| **Table** 一式 | SC-02「結果テーブル」／SC-06「ソース一覧テーブル」／SC-07「ジョブ一覧テーブル」／SC-05 文書一覧 | `<table>` **19**（`sc-*.html`。`index.html` を含めると 20）・`table`（19） | `<table>` 10（`<th>` 50 / `<td>` 50） | **移植** |
| **Card**（＝モックの `panel`） | 各画面の区画（SC-03 属性・タグパネル／バージョン履歴パネル、SC-08 結果パネル、SC-10 統計） | `panel`（48）・`stat`（8） | 素の `<section>` | **移植** |
| **Alert**（＝モックの `note`） | SC-05「必須属性未設定は保存拒否」・SC-06「認証情報は Vault 管理」の注記／SC-06 同期異常の**警告（琥珀）** | `note`（45）・`err`（16）・`warn`（10）・`ok`（18） | `notice` state ＋ `ErrorList` | **移植** |
| **Tabs**（＝モックの `seg`） | **明示なし**（SC-09 §主要素 は「属性体系エディタ、タグ辞書、辺の型辞書、ポリシー定義」の 4 区画を挙げるだけで、**「タブ」とは書いていない**。SC-02 §主要素 の「検索モード切替（キーワード｜意味）」も同型の切替である） | `seg`（4: SC-09 ×2・SC-18・SC-21）・`seg-opt`（14）。**hi-fi の SC-09 が 4 区画を `seg`／`seg-opt` の切替として描いており、注記本文で「『辺の型』タブ」と呼ぶ**（＝タブという呼称の出所は本文ではなくモックアップである） | — | **移植**（根拠は (b) 一本） |
| Dialog | **SC-19 / SC-20 のみ**（公開範囲変更・完全削除確認・緊急アクセス・一括失効） | SC-19 / SC-20 のみ | — | **見送り**（下記） |
| Badge | 状態表示（SC-06 同期状態・SC-07 ジョブ状態） | `tag`（120） | `StatusBadge` | 既存 |
| Button | 全画面 | `btn`（72） | `Button` | 既存 |

**Dialog を見送る根拠**（切り捨てではない）: 計画で「確認ダイアログ」を要求しているのは
`05_screens/01_screens.md` の **SC-19（公開範囲変更・完全削除確認・緊急アクセス）と SC-20（一括失効）だけ**である
（実測: `grep -rn "モーダル\|ダイアログ" planning/projects/microservices-platform --include='*.md'` は **9 件**——
01_screens が 7 件〔**SC-19 節 5 件・SC-20 節 1 件・§変更履歴 1 件〔SC-19 の記述〕**〕、
ADR-0037〔Obsidian 同期方式〕が 2 件。**他の SC 節にはヒットが無い**）。両画面は **FR-19 / FR-20** に属し、
[IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md) 決定 1 が
「FR-17〜FR-21 の実装には着手しない。保留の対象は当該 FR を実現するプロダクトコードと、
**その受け入れを担う画面**・API・データモデル」と定めている。保留中の画面のためだけに部品を先回りで
作ることは、CLAUDE.md の禁止事項「計画外の機能追加・過剰な抽象化」に当たる。
**引き受け先は #452**（FR-19 / FR-20 の保留解除後に当該画面と同時に足す）。

**API の形**（IADR-0121 決定 4 の「入れない」規則を守る）:

- 公開面は `src/packages/ui/src/index.ts` の **1 ファイルのまま**（深い参照は ESLint が禁止済み）。
- どの部品もドメイン語彙・BFF 通信・ルーティング・認証・実行時 config を持たない。
  **文言も持たない**——プリミティブが文言を持つと i18n の入口が 2 つになるため、
  表示文字列は呼び出し側（feature / foundation）から `children` / `props` で渡す。
- `Alert` の状態表現は `StatusBadge` と同じ作法で**アイコン ＋ テキストラベルを型で強制**する
  （INDEX 決定 21。色だけで意味を持たせない）。

### 2. Lingui（ja / en）

#### 2.1 ビルド経路（コンパイル時抽出）

- マクロ（`@lingui/react/macro` の `<Trans>` / `@lingui/core/macro` の `msg` / `t`）を
  **`@lingui/babel-plugin-lingui-macro`** で変換する。`@vitejs/plugin-react` は既に babel を通すため、
  `babel.plugins` へ 1 行足すだけで済む（`src/vitest.config.ts` と
  `src/platform/frontend/vite.config.ts` の 2 か所。両方が同じ変換を通ることが要件）。
- **`@lingui/vite-plugin` は使わない**。当該プラグインは `.po` を直接 import するためのもので、
  peerDependencies に `rolldown` / `@rolldown/plugin-babel` を要求する。
  コンパイル済みカタログ（`lingui compile`）を素の TS モジュールとして import すれば同じことができ、
  ツールチェーンを増やさずに済む（判断は IADR-0125）。

#### 2.2 カタログの置き場と形式

`13_frontend-stack` §ディレクトリ構成 の `locales/ # ja / en（Lingui）` に従う
（**ただし配置は平坦ではなく `foundation/` の下である**。後述 §計画書との差異）。

```text
src/platform/frontend/src/foundation/i18n/
├── index.ts            i18n シングルトン・activate/detect の公開面
└── locales/
    ├── ja/messages.po  翻訳の単一情報源（人が編集する）
    ├── ja/messages.ts  lingui compile の生成物（コミットする）
    ├── en/messages.po
    └── en/messages.ts
```

`sourceLocale` は `ja`（既存文言が日本語のため）。`ja` の `msgstr` も**空にせず明示的に埋める**——
空を許すと「ja は常に緑」になり、検査が en だけの片肺になるためである。

#### 2.3 未翻訳キーの CI 検出（受け入れ基準の核）

**既存の作法（orval 生成物をコミットし CI が再生成差分を検査する。IADR-0121 決定 3）へ揃える。**
3 段で守る。

| # | 検査 | コマンド | 落ちる条件 |
| --- | --- | --- | --- |
| 1 | **カタログの網羅**（キーの取りこぼしを検出） | `pnpm run i18n` ＋ `git diff --exit-code` | ソースに新しいメッセージを足してカタログを更新し忘れた |
| 2 | **未翻訳の不在**（訳文の空白を検出） | `node scripts/check-i18n-catalogs.js` | いずれかのロケールの `msgstr` が空／`fuzzy`／`obsolete` が残っている |
| 3 | **コンパイル時の追加網** | `lingui compile --strict`（`pnpm run i18n` に含む） | Lingui 自身が欠落を検出した |

検査 2 を自前スクリプトにする理由は IADR-0125 に記す（要点: Lingui の `--strict` は
`sourceLocale` の扱いに依存し、「ja は空でも通る」経路が残るため、
**全ロケールの `msgstr` 非空**という単純で自明な不変条件を独立に固定する）。
`scripts/check-i18n-catalogs.js` は既存の検査器の作法に倣う（外部依存ゼロ・`--self-test` を持つ・
`lib/ci-annotate.js` を使う・fail-closed）。

**実効性の確認**: 未翻訳キーを実際に作り、検査 1 / 2 / 3 のどれが落ちるかを実測して §検証 に記録する。

#### 2.4 適用範囲（今回 i18n 化する文言）

**platform の foundation（＝ #452 が作り直さない土台）に限る。**

| 対象 | 文言 |
| --- | --- |
| `foundation/ui/Layout.tsx` | ブランド表示名・サインアウト・アカウント設定のラベル・主要ナビゲーション（`aria-label`）・利用者名の既定 |
| `foundation/routing/nav.ts` | 左ナビのグループ見出し（利用者 / 個人 / 管理 / 運用 / その他） |
| `foundation/ui/NotFound.tsx` | 見出し・本文 |
| `foundation/ui/ErrorBoundary.tsx` | 見出し・既定メッセージ |
| `foundation/ui/notifications.tsx` | 4 種のラベル（成功 / 情報 / 注意 / エラー） |
| `foundation/auth/LoginPage.tsx` | ブランド・説明・サインインボタン |
| `foundation/auth/CallbackPage.tsx` | 処理中・失敗メッセージ |

**既存 11 画面（SC-01〜11）の文言は触らない。** #452 が Page を作り直すため、いま入れると同じ画面を
2 回書く。**繰り延べであって放棄ではない**——引き受け先は #452 であり、本書 §親への申し送り と
#454 のチェックリストへ明記する。

**AST（`src/ai-stock-trading`）は対象外**（別プロジェクトの submodule。本リポから変更できない。IADR-0120）。

#### 2.5 ロケールの切替

- **切替の UI は作らない。** `05_screens/01_screens.md` で言語切替を要求しているのは **SC-13
  （Keycloak のログインテーマ）だけ**であり、共通シェル（§共通シェル）には言語切替の要素が無い。
  無い UI を先回りで作らない（CLAUDE.md 禁止事項）。
- 実行時は `foundation/i18n` が **`navigator.language` から ja / en を判定して活性化**する
  （未対応言語は `ja` へ倒す）。切替そのものは `activate(locale)` の公開 API で行え、
  単体テストが ja / en 両方の描画を固定する。UI を置く必要が生じたら #452 が共通シェルへ足す。

### 3. Storybook

#### 3.1 置き場と構成

`@platform/ui` のカタログであるため `src/packages/ui/.storybook/` に置く。
フレームワークは `@storybook/react-vite`（ワークスペースの Vite 6 と整合）。

#### 3.2 外部 CDN・テレメトリの遮断（08_data-egress-policy）

- `main.ts` で **`core.disableTelemetry: true`** を設定する（Storybook は既定でテレメトリを送る。
  08_data-egress-policy §非LLM外部送信の統制 の「既定テレメトリをオプトアウトする」に該当）。
- **Autodocs（`docs`）は使わない。** docs エントリの生成は `@storybook/addon-docs` に依存し、
  アドオンを入れない方針のままでは設定を置いても 1 件も生まれない（実測: `index.json` の
  entries は story 型 7 件のみ）。「設定はあるが効いていない」状態を残さないため設定ごと落とす。
  スタイルは `@platform/ui/styles.css`（Tailwind v4 ＋ システムフォント）のみを読み、
  **外部フォント・外部 CDN を読む設定は入れない**。
- **「設定した」で終わらせない**——ビルド成果物を走査して外部ホストへの参照が無いことを実測し、
  それを機械検査（`scripts/check-static-egress.js`）として恒久化する。

#### 3.3 `scripts/check-static-egress.js`（新設）

静的ビルド成果物に**外部ホストからネットワーク取得する参照**が無いことを検査する。
対象は Storybook の静的ビルドだけでなく **SPA のビルド成果物（`platform/frontend/dist`）にも同じ規則が
効く**——08_data-egress-policy の統制対象は「SPA フロントエンド」そのものだからである。

- 検出するもの（＝実際に取りに行く参照）:
  `<script src>` / `<link href>` / `<img src>` / `@import url()` / `url()` in CSS / `<iframe src>` が
  **外部オリジン**（`http:` / `https:` / プロトコル相対 `//`）を指す場合。
- 加えて、**既知の禁止ホスト**（`fonts.googleapis.com` / `fonts.gstatic.com` / `cdn.jsdelivr.net` /
  `unpkg.com` / `www.google-analytics.com` 等）は、どこに現れても（JS 文字列の中でも）違反とする。
- 検出しないもの: XML 名前空間 URI（`http://www.w3.org/2000/svg`）・JSON Schema の `$schema`・
  エラーメッセージ中のドキュメント URL。**これらは「取りに行かない」文字列**であり、
  含めると検査が誤検出だらけになって無効化される。除外は**用途ではなくパターンで**書く。
- `--self-test` を持ち、正例（自己ホストのみ）と負例（外部 `<script src>` / Web フォント）で
  検査ロジック自体を試験する。**fail-closed**: `--require <dir>` を付けたのに成果物が無ければ落ちる。

#### 3.4 ESLint プラグイン

13_frontend-stack §採用技術一覧 の Linter 欄「TanStack / Testing Library / **Storybook / Lingui** の
プラグインを併用」に従い、`eslint-plugin-storybook`（stories 向け）と `eslint-plugin-lingui`
（未国際化リテラルの検出）を導入する。**`eslint-plugin-lingui` の適用先は i18n 化した
foundation のファイルに限る**——#452 が作り直す 11 画面へ及ぼすと、いま直せない大量の error が出る
（適用範囲を広げるのは #452 の作業）。

### 4. カバレッジ床

[IADR-0118](../adr/IADR-0118_backend-coverage-floor.md) / [IADR-0034](../adr/IADR-0034_frontend-coverage-gate.md)
の ratchet に従い**引き下げない**。現行は lines/statements 86 / branches 77 / functions 79。

Storybook の設定ファイル（`.storybook/**`）と stories（`**/*.stories.tsx`）は
**計測母数から除外する**（`src/test/**` / `foundation/testing/**` と同じ理由——カタログの足場を
母数へ入れると被覆率が**カタログの行数で動く**。stories は実行されないので、
**足すほど被覆率が下がり、消すほど上がる**——成果物の品質と無関係な動き方である
（`src/test/**` の「足場を数えると床が上がる」とは向きが逆であり、理由文を流用しない））。
**除外が床を甘くしていないことを、除外あり／なしの両方で実測して確認する**（#490 の先例）。

## 受け入れ基準

issue #496 §受け入れ基準 の 5 件を検証可能な形へ展開する。

- [x] **`@platform/ui` の公開面が `index.ts` 1 ファイルのまま**（IADR-0121 決定 4）:
      `src/packages/ui/package.json` の `exports` が `.`（= `./src/index.ts`）と `./styles.css` の 2 つのままで、
      新設した部品がすべて `src/index.ts` から再エクスポートされている。深い参照の禁止は ESLint 既設。
      **発火確認**: 深い参照を書いた違反ファイルで `eslint` が error を出すことを実測する。
- [x] **ja / en の切替が動作し、未翻訳キーを CI で検出できる**:
      (1) 同一コンポーネントが `activate('ja')` / `activate('en')` で別の文言を描画することを単体テストで固定、
      (2) **実際に未翻訳キーを作り、§2.3 の検査が落ちることを実測**して本書へ記録する。
- [x] **Storybook がビルドでき、外部 CDN を読まない**:
      `pnpm --filter @platform/ui run build-storybook` が成功し、
      `node scripts/check-static-egress.js --require src/packages/ui/storybook-static` が green。
      **走査結果（対象ファイル数・検出 0 件）を測定条件つきで本書へ記録する。**
      **発火確認**: 外部 `<script src>` を仕込むと落ちることを実測する。
- [x] **カバレッジ床を割らない**: `src/vitest.config.ts` の `thresholds`（86 / 77 / 79）を下げない。
      実測値を測定条件つきで記録し、新設した除外については除外あり／なしの両方を記録する。
- [x] `pnpm run typecheck` / `lint` / `test` / `test:coverage` / `build` が green。
- [x] `node scripts/check-doc-links.js` / `check-commit-messages.js --base origin/develop` /
      `check-unit-dependencies.js` / `check-test-traceability.js` /
      `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が green。
- [x] AST（submodule）の typecheck / lint / テストが**無改修で**通る。

## テスト方針

- **プリミティブ（新規）**: 各部品につき、(1) 既定の描画、(2) バリアント／状態の反映、
  (3) `className` の合成（`cn()` 経由で呼び出し側の指定が勝つ）、(4) a11y の要点
  （`Label` と入力の関連付け・`Tabs` のロール／選択状態・`Alert` の `role` とアイコン＋ラベル）を固定する。
  **`Alert` は「テキストラベル無しでは組み立てられない」ことを型と実行時の両方で見る**
  （`StatusBadge` と同じ作法。INDEX 決定 21）。
- **i18n（新規）**: (1) `detectLocale()` の判定（ja / en / 未対応 → ja）、
  (2) `activate('en')` 後に foundation の主要コンポーネントが英語で描画されること、
  (3) カタログに全メッセージ ID が存在すること。
  **テストにカタログの写しを書かない**——写すとテストが自分の写しを検査する（#490 の指摘と同型）。
- **検査スクリプト（新規）**: `check-i18n-catalogs.js` / `check-static-egress.js` の純粋ロジックを
  `scripts/scripts.repo.test.js` から単体テストし、各スクリプトの `--self-test` も走らせる。
- **変異試験**: 「壊すと落ちる」ことを実測する。未翻訳キーの混入・外部 CDN 参照の混入・
  `Alert` のラベル省略の 3 つで確認する。

## 検証（実測）

**測定条件**: worktree `feat/ADR-0031-ui-i18n-storybook`（`origin/develop` `4147899` 基点）／
Node 22.22.2 ／ pnpm 10.33.0 ／ Vitest 3.2.7（v8 provider）／ TypeScript 5.9.3 ／ Vite 6.4.3 ／
Lingui 6.6.0 ／ Storybook 10.5.6 ／ `@radix-ui/react-tabs` 1.1.21 ／
**submodule `src/ai-stock-trading`（pin `655e2ed`）と `planning`（pin `df8bce5`）は populate 済み**。

| 検査 | コマンド | 結果 |
| --- | --- | --- |
| 型検査 | `pnpm run typecheck` | green（4 パッケージ。AST は**無改修**） |
| lint | `pnpm run lint` | green（**0 errors / 8 warnings**。warning は全件 `react-refresh/only-export-components`。移行前は 0 errors / 5 warnings で、増えた 3 件は `Input` / `Select` / `Textarea` が cva のバリアントを併せて export するため） |
| 単体テスト | `pnpm run test` | **44 files / 382 tests** 全 green（本作業前は 43 files / 364 tests） |
| カバレッジ | `pnpm run test:coverage` | 後述（床を 86/79/77 → **87/81/77** へ引き上げ） |
| ビルド | `pnpm run build` | green（`dist/assets/index-*.js` 544.74 kB / gzip 161.17 kB） |
| Storybook ビルド | `pnpm --filter @platform/ui run build-storybook` | green（成果物 **28 ファイル**。うち走査対象のテキスト系が 20、非走査の woff2 等が 8） |
| E2E | `playwright test`（後述の条件） | **6 tests 全 green** |
| codegen 乖離 | `pnpm run codegen` ＋ `git diff --exit-code` | green（差分なし） |
| i18n 乖離 | `pnpm run i18n` ＋ `git diff --exit-code` | green（差分なし） |
| i18n カタログ | `node scripts/check-i18n-catalogs.js` | green（2 ロケール・未翻訳 0 件） |
| 静的 egress | `node scripts/check-static-egress.js --require …` | green（Storybook 20 / SPA `dist` 4 ファイル） |
| ドキュメントリンク | `node scripts/check-doc-links.js` | green（410 件） |
| ユニット依存方向 | `node scripts/check-unit-dependencies.js` | green |
| テスト・トレーサビリティ | `node scripts/check-test-traceability.js` | green |
| コミット件名 | `node scripts/check-commit-messages.js --base origin/develop` | green |
| スクリプト自己試験 | `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | green（**244 tests**。本作業前は 239） |

### 受け入れ基準 1: `@platform/ui` の公開面が `index.ts` 1 ファイルのまま

`src/packages/ui/package.json` の `exports` は `.`（= `./src/index.ts`）と `./styles.css` の **2 つのまま**で
変えていない。新設した 8 部品はすべて `src/index.ts` から再エクスポートしている。
Storybook の stories も**公開面だけを通して**部品を参照する（`from '../index'`）——
深い参照で書くと、公開面へ載せ忘れた部品がカタログには現れる（「カタログにあるのにアプリから使えない」）。

**発火確認（実測）**: `import { Button } from '@platform/ui/src/components/Button';` を書いた違反ファイルを
`platform/frontend/src/` へ一時的に置き、`pnpm exec eslint <file>` が
`no-restricted-imports` の error を **1 件**（メッセージ:「@platform/ui の内部実装を直接参照しない。
公開面は "@platform/ui" と "@platform/ui/styles.css" のみ。」）出すことを確認して削除した。

### 受け入れ基準 2: ja / en の切替と未翻訳キーの CI 検出

**切替が動くこと**は `foundation/i18n/i18n.test.tsx`（18 ケース）が固定する。
同一コンポーネント（`NotFound`）が `activate('ja')` / `activate('en')` で
「見つかりませんでした」/「Not found」を描き分けること、通知のラベルが「注意」/「Warning」に
変わること、`detectLocale` が `ja` / `ja-JP` / `en-US` / `EN` / `fr-FR` / `[]` を正しく判定することを見る。

**未翻訳キーの検出**は 3 段（IADR-0125 決定 4）で、**実際に未翻訳キーを作って各段が落ちることを実測した**。
`✗` = 検出（exit 1）、`—` = 素通り（exit 0）。

| シナリオ | 検査 1<br>`pnpm run i18n` ＋ `git diff` | 検査 2<br>`check-i18n-catalogs.js` | 検査 3<br>`lingui compile --strict` |
| --- | --- | --- | --- |
| **A**: ソースへ `msg` を足したがカタログ未更新 | **✗** | — | — |
| **B**: カタログ更新済み・**`en` の `msgstr` が空** | — | **✗** | **✗** |
| **C**: カタログ更新済み・**`ja`（sourceLocale）の `msgstr` が空** | — | **✗** | — |

**「差分検査だけでは受け入れ基準を満たさない」ことが実測で確かめられた**（シナリオ B の検査 1）。
`lingui extract` は未訳を `msgstr ""` の空エントリとして生成するのが正常動作であり、
再実行しても差分が出ない（`git diff --quiet` が exit 0）。
また `--strict` は `sourceLocale` を見ない（シナリオ C）。**3 者はいずれも他を置き換えない。**

**本番でも実際に働いた**: 作業中に `RequireAuth` / `RequireRole` の「読み込み中…」を i18n 化して
カタログを更新し忘れたところ、検査 1（`pnpm run i18n` ＋ `git diff`）が差分を出して止めた
（是正はコミット `6a2f820`「RequireAuth / RequireRole の『読み込み中…』をカタログへ反映し ja/en を埋める」。
検出経路は同コミットの本文にも残した——スカッシュ後も追える形にするためである）。

付随的な実測として、`@lingui/format-po@6.6.0` は `POT-Creation-Date` に**実行時刻を毎回書く**ため、
そのままでは検査 1 が常に赤になる（連続 2 回の extract で `.po` の md5 が変化）。
`src/lingui.config.ts` で当該行を落とすラッパを噛ませて決定的にした（IADR-0125 §実測）。

### 受け入れ基準 3: Storybook がビルドでき、外部 CDN を読まない

`pnpm --filter @platform/ui run build-storybook` が成功する。
成果物は **28 ファイル**で、うち走査対象（テキスト系の拡張子）が **20 ファイル**である
（残り 8 は自己ホストの woff2 等のバイナリ）。走査結果:

| 種別 | 実測 |
| --- | --- |
| リソースタグ（`<link>` / `<script>` / `<img>` ほか）の外部参照 | **0 件** |
| CSS（`.css` ＋ HTML のインライン `<style>`）の `@import` / `url()` の外部参照 | **0 件** |
| 既知の禁止ホスト（フォント CDN・汎用 CDN・analytics・エラー報告 SaaS） | **0 件** |
| 外部オリジンの `<a href>`（**違反ではない**＝押して初めて起きる遷移） | 4 件（すべて `https://storybook.js.org/docs/…` の説明リンク） |
| Web フォント | **自己ホスト**。`./sb-common-assets/nunito-sans-*.woff2` を成果物に同梱し、`index.html` のインライン `<style>` から相対パスで参照している |

SPA 本体（`platform/frontend/dist`・4 ファイル）も 0 件である。
テレメトリとクラッシュレポートは `.storybook/main.ts` の `core.disableTelemetry` /
`core.enableCrashReports: false` で無効化した（08_data-egress-policy §非LLM外部送信の統制）。

**発火確認（変異試験・実測）**: `.storybook/preview-head.html` に
`<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Inter&display=swap">` を仕込んで
再ビルドすると、`check-static-egress.js` が **2 件**（リソースタグ ＋ 既知の禁止ホスト）を報告して
exit 1 になった。除去して再ビルドすると exit 0 に戻る。

なお走査の設計上、**HTML のインライン `<style>` にも CSS 規則を当てている**。
当初は `.css` / `.js` だけに当てていたが、Storybook の実成果物がインライン `<style>` から
フォントを参照していることを実測で知り、外部フォントが**そこに書かれたら見逃す**ため広げた。

### 受け入れ基準 4: カバレッジ床を割らない

| 集計 | lines/statements | branches | functions |
| --- | --- | --- | --- |
| 全ユニット横断（本 PR・除外あり） | **93.86%** | **84.11%** | **86.58%**（厳密 86.5889%） |
| MSP 所有分（本 PR・除外あり） | **92.04%** | **82.93%** | **86.08%** |
| MSP 所有分（本 PR・**除外なし**） | 87.96% | 82.95% | 86.13% |
| （参考）移行前 `4147899` の全ユニット横断 | 93.79% | 83.54% | 85.53% |
| 床 | 86 → **87** | 77 → **77**（据え置き） | 79 → **81** |

**引き下げはしていない。** 既存の導出規則（MSP 所有分の実測から 5pt 下・切り捨て）をそのまま適用した。
MSP 所有分は lcov から AST のファイルを除いて再集計した値である。

新たに `**/*.stories.{ts,tsx}` と `**/.storybook/**` を計測母数から除外した。
**この除外は床の水準を実際に動かす**（#490 で足した `foundation/testing/**` と違い「動かしていない」とは
言えない）——除外なしなら同じ導出規則から出る床は **lines 82 / branches 77 / functions 81** であり、
lines だけが 5pt 甘くなる。差は stories 1 ファイル（145 行・テストから実行されない）に由来する。
除外を採るのは、**カタログの行数が被覆率を左右する状態そのものが誤り**だからである
（stories を消すと床が上がるという、成果物の品質と無関係な動き方をする）。
**除外なしの実測でも移行前の床 86 は満たしている**（87.96% > 86）ため、
この除外は「床を割るのを避けるための除外」ではない。

**記録（被覆率の読み方）**: 新設した 8 プリミティブの**本番の呼び出し元は現時点で 0 件**である
（参照は専用テストと stories のみ。画面へ適用するのは #452）。#490 の `notify` と同型の留保であり、
床の引き上げ分の一部は「まだ利用者のいないモジュールの専用テスト」に由来する。

### 受け入れ基準 5: 一括の検査

上表のとおり `typecheck` / `lint` / `test` / `test:coverage` / `build` および
リポジトリの検査スクリプトが全て green である。

### E2E（実測）と、そこで見つかった退行

`platform/frontend/e2e/` の 6 本が **全 green**。ただし**最初の実行では 1 本が落ちた**——
ロケール検出（IADR-0125 決定 7）を入れた結果、Playwright の既定ロケール（`en-US`）で
SPA が英語で描画され、日本語の見出しを待つスモークが失敗した。次の 2 点で是正した。

1. `playwright.config.ts` に `use.locale = 'ja-JP'` を置く（単体テスト側を `setup.ts` で
   `activate('ja')` に固定しているのと同じ理由・同じ値）。
2. **ブランド表示名（「汎用プラットフォーム」）は en カタログでも訳さない**（後述 §計画書との差異）。

**固定が load-bearing であることの変異試験（実測）**: `login.smoke.spec.ts` へ
「社内ナレッジ検索・AI 回答プラットフォーム」（= 訳される文言）のアサーションを足したうえで、
`locale: 'ja-JP'` を外すと当該テストが**失敗**し、戻すと通る。
2 のとおりブランド名は訳さないため、ブランド名のアサーションだけでは固定の有無を検出できない
——「設定は入れたが誰も検査していない」状態を作らないための追加である。

**この環境では `playwright install` がブラウザを取得できない**。インストール済みの
`/opt/pw-browsers/chromium-1194` を `launchOptions.executablePath` で指すローカル専用 config を
一時的に置いて実走し、確認後に削除した。**CI（`frontend.yml`）は
`playwright install --with-deps chromium` を実行するため、リポジトリの `playwright.config.ts` の
`locale` 追加以外に変更は無い。**

### AST（別プロジェクト）への影響（実測）

**無改修で green**: typecheck（`tsconfig.standalone.json`）/ lint / テストがすべて通る。
本作業は `@platform/ui` の追加・platform foundation の文言・ワークスペース設定に閉じており、
AST の features には触れていない。Lingui の抽出対象からも AST を外している
（`lingui.config.ts` の `include` は platform / knowledge のみ。IADR-0120）。

## 計画書との差異

| 事項 | 計画・issue の記載 | 実装 | 根拠 |
| --- | --- | --- | --- |
| **Dialog の移植** | issue #496 §スコープ が「Input・Select・**Dialog**・Table・Tabs 等」と例示 | **移植しない** | 計画（`01_screens`）で確認ダイアログを要求しているのは **SC-19 / SC-20 のみ**（実測: `grep -rn "モーダル\|ダイアログ" planning/projects/microservices-platform --include='*.md'` は **9 件**——01_screens が 7 件〔**SC-19 節 5 件・SC-20 節 1 件・§変更履歴 1 件（SC-19 の記述）**〕、ADR-0037〔Obsidian 同期方式〕が 2 件。**他の SC 節にはヒットが無い**）。両画面は FR-19 / FR-20 に属し、[IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md) 決定 1 が「保留の対象は当該 FR を実現するプロダクトコードと、**その受け入れを担う画面**」と定めて着手を保留している。issue の記載は「等」を伴う**例示**であり、計画本文が要求していない部品を先回りで作ることは CLAUDE.md の禁止事項に当たる。**繰り延べであって放棄ではない**——引き受け先は #452（保留解除後） |
| **Select の実装方式** | ADR-0031「UI コンポーネント = shadcn/ui」 | shadcn/ui 標準の `@radix-ui/react-select` ではなく**ネイティブ `<select>`** を cva で装う | 計画が要求しているのは「定義済み区分のみ」「権限内のタグ／フォルダのみ」という**値の選択**であり、ネイティブで満たせる。ネイティブはモバイル・スクリーンリーダ・キーボードの既定挙動をそのまま得られる。`Tabs` は逆に a11y を自前で書くと誤りやすいため Radix を採った（IADR-0125 決定 1） |
| **Label の実装方式** | ADR-0031「UI コンポーネント = shadcn/ui」 | shadcn/ui 標準の `@radix-ui/react-label` ではなく**素の `<label>`** | `Select` と同じ理由である。shadcn/ui の `Label` が Radix を使うのは「ラベル押下時のテキスト選択を抑止する」等の細部のためで、計画が要求しているのは「入力 / バリデーション」表の項目名の表示と入力との関連付けである。素の `<label>` ＋ `htmlFor` で満たせ、実行時依存を増やさない。**`Select` の逸脱だけを記録して `Label` を書かないのは非対称**であるため併記する（監査指摘） |
| **カタログの置き場** | `13_frontend-stack` §ディレクトリ構成 は `src/locales/`（平坦） | `platform/frontend/src/foundation/i18n/locales/` | 計画の §ディレクトリ構成 は「ユニット内 SPA」の**素朴な例示**であり、本リポジトリの実装は基盤を `foundation/` の下へ束ねる構成を既に採っている（[IADR-0121](../adr/IADR-0121_spa-stack-migration-staging.md) が Superseded にした [IADR-0033](../adr/IADR-0033_frontend-spa-foundation.md) 以来の配置。`api` / `auth` / `routing` / `ui` / `config` がすべて `foundation/` 配下）。**平坦構成へ読み替えるのではなく、既存の構成規則に合わせて `foundation/i18n/locales/` とした。** `locales/` に ja / en を並べるという計画の要点は満たしている |
| **i18n の適用範囲** | issue #496「既存文言の抽出とカタログ整備」 | **platform の foundation に限る**（既存 11 画面は触らない） | #452 が SC-01〜11 の Page を作り直す（[#490 仕様書 §親への申し送り](./20260804_issue-490_spa-router-shell.md)）。いま `<Trans>` を入れると**同じ画面を 2 回書く**——[IADR-0121](../adr/IADR-0121_spa-stack-migration-staging.md) 決定 1 が第 2 段の分割で守っている原則そのものに反する。**繰り延べであって放棄ではない**（引き受け先 #452。IADR-0125 決定 6） |
| **ロケール切替の UI** | 受け入れ基準「ja / en の切替が動作し」 | **UI は作らない**。`navigator.language` から判定し、切替は `activate(locale)` の公開 API | `01_screens` で言語切替を要求しているのは **SC-13（Keycloak のログインテーマ）だけ**であり、§共通シェル の要素に言語切替は無い。無い UI を先回りで作らない（IADR-0125 決定 7）。切替が動くことは単体テストが固定する |
| **ブランド表示名の翻訳** | `01_screens` §共通シェル「ブランド表示名は『汎用プラットフォーム』で統一する」「**ブランド名は差し替えない**」 | en カタログでも**訳さない**（同じ文字列を入れる） | 「差し替えない」は別ホスト・可変ユニット間の統一を述べた文脈だが、**言語による差し替えの可否は計画側が判断していない**。実装が独断で英語名を作ると、計画の「統一」に反する既成事実になる。安全側に倒し、§未決事項 として計画へ問う |
| **マクロの使い方** | Lingui の標準は `<Trans>` | foundation では `i18n._(msg`…`)` に統一（`<Trans>` は使わない） | `<Trans>` は `I18nProvider` を必須とし、素で描画する単体テスト 31 件が wrapper の有無で落ちる（実測）。foundation が出すのは素の文字列だけでリッチテキスト・複数形を使わない。`I18nProvider` は `App.tsx` に残し、画面側（#452）が `<Trans>` を使えるようにしてある |

## ワークフロー変更（本 PR に含まれる）

`.github/workflows/` は GitHub App 権限で編集できないため、**下記は人間（親）がローカル権限で
コミットした**。内容は本 PR に含まれる。新設した検査を CI に結線しないと、
「検査器は作ったが誰も走らせない」状態になり退行を止められないためである。

1. **`frontend.yml`（`build-test` ジョブ）へ i18n の乖離検査を足した**（codegen の直後）。
   ```yaml
   # ADR-0031 / IADR-0125 決定 4: Lingui のカタログはコミットしている。
   # 再抽出・再コンパイルして差分が出るなら、ソースとカタログが乖離している。
   - name: i18n catalogs are up to date (lingui)
     run: |
       pnpm run i18n
       git diff --exit-code -- platform/frontend/src/foundation/i18n/locales
   - name: i18n catalogs have no untranslated keys
     working-directory: ${{ github.workspace }}
     run: node scripts/check-i18n-catalogs.js
   ```
2. **`frontend.yml`（`build-test` ジョブ）の Build の後へ Storybook ビルドと egress 検査を足した。**
   ```yaml
   - name: Build Storybook (catalog)
     run: pnpm --filter @platform/ui run build-storybook
   # 08_data-egress-policy / IADR-0125 決定 5: 成果物に外部オリジンからの取得が無いこと。
   - name: No external egress in build artifacts
     working-directory: ${{ github.workspace }}
     run: >
       node scripts/check-static-egress.js
       --require src/packages/ui/storybook-static
       --require src/platform/frontend/dist
   ```
3. **`ci.yml` の `scripts-tests` ジョブは変更不要**（`scripts.repo.test.js` が新スクリプトの
   `--self-test` と実データ検査を呼ぶ。`REQUIRE_REPO_TESTS=1` は既設）。
4. **`frontend.yml` の `paths` へ `src/lingui.config.ts` を追加した**（push / pull_request の両方）。
   追加ファイルの大半は `src/*/frontend/**` か `src/packages/**` に当たるが、
   `src/lingui.config.ts` だけは `src/` 直下で既存フィルタに当たらない。
   「単独で変わることは稀だから実害は無い」で済ませず厳密にした——
   **走らない経路を残すこと自体が「検査したつもり」を作る**ためである。

`frontend-tests.yml`（単体テスト＋カバレッジ）は変更不要である（`src/vitest.config.ts` と
`src/packages/**` が既存フィルタに入っている）。

## 親への申し送り

### 第 2 段の完了について

**第 2 段（IADR-0121 決定 1）の 6 項目のうち、本 PR で残り 3 項目を消化した。**

| 第 2 段の項目 | 消化先 |
| --- | --- |
| TanStack Router 移行 / 共通シェル / 旧 13 画面のルート載せ替え | #490（PR #495） |
| shadcn/ui コンポーネント本移植 / Lingui(ja/en) / Storybook | **本 issue #496** |

ただし **#490 が #452 へ渡した「旧 13 画面の削除・再実装」は依然として未達**であり、
第 2 段の完了条件（`feedback/20260804_frontend-migration-staging-interpretation.md` §完了条件）は
**#452 の消化まで満たされない**。

### #452 が引き受ける項目（本 PR で意図的に触れなかったもの）

| 項目 | 内容 |
| --- | --- |
| **`Dialog` の移植** | FR-19 / FR-20 の着手保留（IADR-0119）が解けたあと、SC-19 / SC-20 と同時に足す |
| **既存 11 画面の文言の i18n 化** | 本 PR は platform の foundation のみ。Page を作り直す際に `<Trans>` / `msg` を入れる |
| **プリミティブの画面への適用** | 8 部品の本番の呼び出し元は現時点で 0 件である |
| **`eslint-plugin-lingui` の適用範囲の拡大** | 現在は i18n 化済みの foundation 配下のみ。画面を i18n 化したら `files` を広げる |
| **ダークテーマのトークン** | 画面が確定してから |
| **ロケール切替 UI** | 計画が要求した場合に共通シェルへ足す（現状 §共通シェル に要素が無い） |

### #454 チェックリストへの追記内容

1. **第 2 段の残り 3 項目（shadcn/ui 本移植・Lingui・Storybook）は本 PR で完了**。
2. **第 2 段の完了は依然 #452 待ち**（旧 13 画面の削除・再実装）。
3. ワークフロー変更（上記 §ワークフロー変更）は**本 PR に含まれる**（`.github/workflows/` は
   GitHub App 権限で編集できないため、人間がローカル権限でコミットした）。

## 未決事項

1. **ブランド表示名を英語ロケールで訳すか**（計画への問い）。`01_screens` §共通シェル は
   「ブランド表示名は『汎用プラットフォーム』で統一する」「ブランド名は差し替えない」と定めるが、
   **言語による差し替えの可否**には触れていない。本 PR は安全側に倒して**訳さない**（en でも
   「汎用プラットフォーム」）。計画側の判断が要る（`/plan-feedback` の候補）。
2. **`I18nProvider` と `<Trans>` の使い分けの明文化**。本 PR は foundation で `msg` に統一したが、
   画面側（#452）が `<Trans>` を使い始めると、素で描画する単体テストは wrapper が要る。
   テスト用の共通 wrapper（`foundation/testing/` のハーネス）に `I18nProvider` を組み込むのが自然だが、
   画面の作り方が決まる #452 で判断するのが適切である。
3. **shadcn/ui の「派生」をどこまで許すか**（計画への問い）。本 PR は `Select`（Radix Select →
   ネイティブ `<select>`）と `Label`（Radix Label → 素の `<label>`）で shadcn/ui の実装基盤から
   離れている。ADR-0031 は「UI コンポーネント = shadcn/ui」としか書かず、**部品ごとの実装基盤
   （Radix への依存）まで確定しているのかが読み取れない**。1（ブランド名の翻訳可否）と並べて
   計画側の判断を仰ぐ（`/plan-feedback` の候補）。**新 ADR は起こさない**——
   計画が部品単位を定めていない以上、実装 ADR（IADR-0125 決定 1）の記録で足りると判断した。
4. **バンドルサイズ**。`index.js` が 544 kB（gzip 161 kB）で Vite の 500 kB 警告に触れる
   （#490 から +7 kB。Lingui ランタイム分）。コード分割は画面が確定する #452 の後が適切である
   （#490 の未決事項 5 を引き継ぐ）。
5. **Storybook のアドオン**。現状 0 個である。a11y チェック（`@storybook/addon-a11y`）等を入れる場合は
   08_data-egress-policy に照らして判断する（判断の材料は `check-static-egress.js` の走査結果が与える）。
