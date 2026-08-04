---
title: SPA 移行 第 2 段の残り — shadcn/ui 派生プリミティブの本移植・Lingui(ja/en)・Storybook
type: spec
status: in-progress
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
モックアップの語彙は `mockups/hi-fi/sc-*.html` の `class="…"` を全抽出して数えた値。

| 部品 | (a) 計画の明示 | (b) hi-fi モックの語彙 | (c) 既存実装 | 採否 |
| --- | --- | --- | --- | --- |
| **Input** | SC-01 質問／キーワード（テキスト・必須）／SC-02 検索ボックス／SC-05 タイトル（テキスト・必須） | `inp`（17）・`input` | `<input>` 18 | **移植** |
| **Textarea** | SC-08「分析内容の入力（**テキストエリア**）」 | 同上（複数行入力） | `<textarea>` 3 | **移植** |
| **Select** | SC-01 対象範囲フィルタ（選択）／SC-05 機密区分（**選択**・必須）／SC-09 対象属性（**選択**・必須）／SC-02 検索モード・並び順の切替 | — | `<select>` 9 | **移植** |
| **Label** | 各画面の「入力 / バリデーション」表の項目名 | `olabel`（27）・`<label>`（29） | `<label>` 28 | **移植** |
| **Table** 一式 | SC-02「結果テーブル」／SC-06「ソース一覧テーブル」／SC-07「ジョブ一覧テーブル」／SC-05 文書一覧 | `<table>` 20・`table`（19） | `<table>` 10（`<th>` 50 / `<td>` 50） | **移植** |
| **Card**（＝モックの `panel`） | 各画面の区画（SC-03 属性・タグパネル／バージョン履歴パネル、SC-08 結果パネル、SC-10 統計） | `panel`（48）・`stat`（8） | 素の `<section>` | **移植** |
| **Alert**（＝モックの `note`） | SC-05「必須属性未設定は保存拒否」・SC-06「認証情報は Vault 管理」の注記／SC-06 同期異常の**警告（琥珀）** | `note`（45）・`err`（16）・`warn`（10）・`ok`（18） | `notice` state ＋ `ErrorList` | **移植** |
| **Tabs**（＝モックの `seg`） | SC-09「属性体系 / タグ辞書 / 辺の型 / ポリシー定義」（本文が「『辺の型』**タブ**」と呼ぶ） | `seg`（2）・`seg-opt`（14） | — | **移植** |
| Dialog | **SC-19 / SC-20 のみ**（公開範囲変更・完全削除確認・緊急アクセス・一括失効） | SC-19 / SC-20 のみ | — | **見送り**（下記） |
| Badge | 状態表示（SC-06 同期状態・SC-07 ジョブ状態） | `tag`（120） | `StatusBadge` | 既存 |
| Button | 全画面 | `btn`（72） | `Button` | 既存 |

**Dialog を見送る根拠**（切り捨てではない）: 計画で「確認ダイアログ」を要求しているのは
`05_screens/01_screens.md` の **SC-19（公開範囲変更・完全削除確認・緊急アクセス）と SC-20（一括失効）だけ**である
（`grep -rn "モーダル\|ダイアログ" planning/projects/microservices-platform` の実測: 本文ヒットは
01_screens の SC-19 / SC-20 節と ADR-0037 のみ）。両画面は **FR-19 / FR-20** に属し、
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

`13_frontend-stack` §ディレクトリ構成 の `locales/ # ja / en（Lingui）` に従う。

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
- `docs`（Autodocs）は使うが、**外部フォント・外部 CDN を読む設定は入れない**。
  スタイルは `@platform/ui/styles.css`（Tailwind v4 ＋ システムフォント）のみを読む。
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
母数へ入れると「stories を足すほど床が上がる」見かけの改善が起きる）。
**除外が床を甘くしていないことを、除外あり／なしの両方で実測して確認する**（#490 の先例）。

## 受け入れ基準

issue #496 §受け入れ基準 の 5 件を検証可能な形へ展開する。

- [ ] **`@platform/ui` の公開面が `index.ts` 1 ファイルのまま**（IADR-0121 決定 4）:
      `src/packages/ui/package.json` の `exports` が `.`（= `./src/index.ts`）と `./styles.css` の 2 つのままで、
      新設した部品がすべて `src/index.ts` から再エクスポートされている。深い参照の禁止は ESLint 既設。
      **発火確認**: 深い参照を書いた違反ファイルで `eslint` が error を出すことを実測する。
- [ ] **ja / en の切替が動作し、未翻訳キーを CI で検出できる**:
      (1) 同一コンポーネントが `activate('ja')` / `activate('en')` で別の文言を描画することを単体テストで固定、
      (2) **実際に未翻訳キーを作り、§2.3 の検査が落ちることを実測**して本書へ記録する。
- [ ] **Storybook がビルドでき、外部 CDN を読まない**:
      `pnpm --filter @platform/ui run build-storybook` が成功し、
      `node scripts/check-static-egress.js --require src/packages/ui/storybook-static` が green。
      **走査結果（対象ファイル数・検出 0 件）を測定条件つきで本書へ記録する。**
      **発火確認**: 外部 `<script src>` を仕込むと落ちることを実測する。
- [ ] **カバレッジ床を割らない**: `src/vitest.config.ts` の `thresholds`（86 / 77 / 79）を下げない。
      実測値を測定条件つきで記録し、新設した除外については除外あり／なしの両方を記録する。
- [ ] `pnpm run typecheck` / `lint` / `test` / `test:coverage` / `build` が green。
- [ ] `node scripts/check-doc-links.js` / `check-commit-messages.js --base origin/develop` /
      `check-unit-dependencies.js` / `check-test-traceability.js` /
      `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が green。
- [ ] AST（submodule）の typecheck / lint / テストが**無改修で**通る。

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

（実装後に記入する）

## 計画書との差異

（実装後に記入する）

## 親への申し送り

（実装後に記入する）

## 未決事項

（実装後に記入する）
