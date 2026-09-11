---
title: IADR-0436 共有 UI への三部品・区画部品の追加と、重なりの部品の土台に Base UI を採る判断
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-12, ADR-0031, SC-01, SC-03, SC-09, SC-10, SC-11, SC-12, SC-17, SC-18, SC-19, SC-20, SC-21, IADR-0121, IADR-0125, IADR-0134, IADR-0435, IADR-0437, IADR-0439]
author: Claude
created: 2026-09-12
updated: 2026-09-12
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/05_screens/mockups/hi-fi/index.html
related_specs:
  - ../specs/20260912_frontend-nocturne-and-experience-gaps.md
---

# IADR-0436: 共有 UI への三部品・区画部品の追加と、重なりの部品の土台に Base UI を採る判断

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-09-12
- 決定者: Claude（実装）／土台の採否は利用者裁定（2026-09-12 裁定 5）

## 起点・関連

- 関連する計画書 ID:
  ADR-0031（計画リポ）（Accepted。UI = Tailwind v4 ＋ shadcn/ui ＋ Lucide）／
  13_frontend-stack（計画リポ）（fixed。§shadcn/ui 派生の範囲＝**4 基準**）／
  05_screens（計画リポ）（§共通シェル §横断の表示規約〔待ち・空・エラー・再試行〕・`mockups/hi-fi/`）／
  INDEX（計画リポ） 決定 21（色だけで意味を持たせない）
- 関連する実装 ADR:
  [IADR-0125](IADR-0125_ui-primitives-i18n-catalog-and-storybook.md)（**本決定が補完する**。
  決定 1 の「移植は 3 情報源の突き合わせで要求が示せるものに限る」という**規則は不変**で、
  決定 2「Dialog は移植しない」の前提が変わった。**supersede ではない**——§関連 を参照）／
  [IADR-0121](IADR-0121_spa-stack-migration-staging.md)（決定 4 = 公開面は `src/index.ts` の 1 ファイル）／
  [IADR-0435](IADR-0435_nocturne-token-layering-and-theme-switch.md)（本部品群が引くトークン）／
  [IADR-0437](IADR-0437_query-state-three-phase-component-and-retry.md)（三部品を**文言つき**で束ねる側）／
  [IADR-0439](IADR-0439_ai-chat-streaming-ux-and-markdown.md)（`Tooltip` の最初の利用者。初期ロードへの影響の実測）／
  [IADR-0134](IADR-0134_spa-route-code-splitting-boundaries.md)（初期チャンクの ratchet）
- 関連する実装仕様書:
  [20260912_frontend-nocturne-and-experience-gaps](../specs/20260912_frontend-nocturne-and-experience-gaps.md)
- 関連 issue: 本 PR（UI/UX 改善）。検討メモ 第 3 弾「UI 部品の現在地」・第 4 弾「体験の穴」（別途送付）

## コンテキストと課題

1. **待ち・空・エラーが画面ごとに違う。** 17 画面がそれぞれ `<p>` 直書きで三状態を描き、
   文言（「読み込み中です。」「読み込み中…」「検索中…」）も `role` の有無も揃っていなかった。
   モックは `.panel` / `.stat` / `.bar` / `.kv` / `.note` / `.hr` という区画の語彙を持つが、
   共有 UI にはどれも無く、各画面が素の `div` で近似していた。
2. **重なりの部品（Dialog / Tooltip）の土台が無い。** `IADR-0125` 決定 2 は「Dialog は移植しない」と
   決めたが、その理由は**着手保留（当時）の要求に属する画面でしか要らなかった**ことである。
   保留は解け、SC-19 / SC-20 の確認ダイアログが自前オーバーレイで実装されており、
   右レールのアイコンボタンには説明（Tooltip）が要る。計画の 4 基準（フォーカストラップ／
   複合キーボード操作／ポータル配置計算／`aria-*` の動的同期）に**両方とも該当する**。
3. **どのライブラリを土台にするか。** 計画は shadcn/ui を挙げるが、shadcn/ui は
   「コピーして所有する」方式であり、**土台となる headless ライブラリは別に選ぶ**必要がある。

## 検討した選択肢

### 論点 A: 三部品・区画部品を共有 UI へ置くか

| # | 案 | 却下理由 |
| --- | --- | --- |
| A1 | **`@platform/ui` へ置く（採用）** | — |
| A2 | 画面ごとに書く（現状維持） | **17 画面の不揃いが問題の本体**である |
| A3 | foundation（`platform/frontend`）へ置く | knowledge / AST から**共有 UI として**引けない。文言を持つ層は別に要る（IADR-0437） |

### 論点 B: 重なりの部品の土台

| # | 案 | 活発性 | a11y | 却下理由 |
| --- | --- | --- | --- | --- |
| B1 | **Base UI `@base-ui/react` 1.8（採用）** | **1.4〜1.8 が月次**（1.8.0 = 2026-09-04 公開。実測） | フォーカストラップ・`inert`・ポータル配置を持つ | — |
| B2 | ネイティブ `<dialog>` | — | `showModal()` は閉じ込めを持つが、**Tooltip の配置計算が無く**、Esc とフォーカス復帰の細部を自前で書くことになる | 部品ごとに別の土台になる |
| B3 | Radix UI | 既に `@radix-ui/react-tabs` を使用中 | 十分 | **Dialog / Tooltip 相当の保守が停滞している**。既存の Tabs は据え置くが、新規の土台には採らない |
| B4 | 旧名 `@base-ui-components/react` | **rc.0 で停止**（実測） | — | 停止したパッケージを新規に入れない |

## 決定

### 決定 1: 待ち・空・エラーの三部品と、モックの区画部品を `@platform/ui` へ足す

`Spinner`（`label` 必須）/ `Skeleton` / `LoadingState` / `EmptyState`（`title` ＋ 任意の
`description` / `action` / `icon`）/ `ErrorState`（`action` ＝ 再試行のスロット）/
`Panel`（`variant: default | ghost`・`heading?`）/ `Stat` / `ProgressBar`（`role="progressbar"`）/
`Kv`・`KvItem` / `Note` / `Rule`。

🔴 **三部品を 1 つにまとめない。** 「0 件」と「失敗」を同じ見た目にすると、利用者は
**再試行すべきか条件を変えるべきか判断できない**。部品を分けることが判断の分岐そのものである。

🔴 **表示文言は持たせない**（IADR-0125 決定 1 を維持）。ラベルは props で受ける。文言を内蔵すると
i18n の入口が 2 つに割れ、カタログの網羅検査（同決定 4）を抜ける。
`role="status"` / `role="alert"` は**部品側が持つ**（呼び出し側が重ねて付けない）。

### 決定 2: `Dialog` / `Tooltip` の土台に Base UI（`@base-ui/react` 1.8）を採る

- **`render` prop 方式**である（Radix の `asChild` ではない）。ラッパはこれをそのまま通す。
- 🔴 **`initialFocus` を必ず通せるようにする。** 本リポジトリの確認ダイアログは
  **取消側に初期フォーカスを当てる**規律を持つ（破壊的操作を Enter の連打で実行させない）。
  ラッパがこの prop を落とすと、**既存の規律が黙って消える**。
- **Base UI は `aria-modal` を付けず、外側を `inert` にする**（より新しい作法）。したがって
  既存テストは `aria-modal` ではなく `role="dialog"` と accessible name / description で測る形へ変えた。
  **テストを緩めたのではなく、測る対象を実装の作法へ合わせた**。
- 旧名 `@base-ui-components/react` は使わない（rc.0 で停止）。

### 決定 3: `vendor-baseui` を `manualChunks` と `requiredChunks` の両方へ足す

`manualChunks` の `ui` 規則は `packages/ui/` を拾うが、**`node_modules/@base-ui/` は拾わない**。
規則を足し、`scripts/chunk-budget-baseline.json` の `requiredChunks` にも足す
（自己試験が両者の完全一致を突き合わせる）。

🔴 **Base UI は初期ロードに入る。追い出せない。** `@platform/ui` の公開面（`src/index.ts`）は
`ui` チャンクに在り、`ui` は**エントリの静的依存＝初期ロード**である。したがって
**どこか 1 箇所で `Dialog` / `Tooltip` を使った時点で `ui` → `vendor-baseui` の静的辺ができ、
`vendor-baseui` が初期ロードへ入る**（遅延ルートからしか使わなくても、`React.lazy` 越しに読んでも
消えない辺である）。実測の経過:

| 時点 | `vendor-baseui` | 初期ロード合計 |
| --- | --- | --- |
| 部品を足しただけ（誰も使っていない） | **36 B**（空チャンク） | 556,010 B |
| 右レールが `Tooltip` を使った | 85,457 B | 656,554 B |
| 確認ダイアログを `Dialog` へ載せ替えた | 114,124 B | 705,386 B |

**部品を足した時点では 0.02 kB しか動かない**（Rollup が落とす）。**跳ねるのは最初の利用者が出たとき**である。

### 決定 4: 既存の `@radix-ui/react-tabs` は据え置く

土台を 2 つ持つことになるが、**動いている部品を土台の統一だけの理由で書き換えない**。
Tabs は 4 基準のうち「複合キーボード操作」に該当して Radix で実装されており、
置き換えても利用者に見える差は無い。**次に Tabs を実質的に触る作業が土台の移行を判断する。**

## 理由

- 決定 1: 問題は「部品が無いこと」ではなく「**画面ごとに違うこと**」だったので、構造（部品 1 本）で潰す。
- 決定 2: 裁定 5 が「Base UI が活発なら採用」を条件としたので、**活発性を実測してから**採った
  （1.4〜1.8 が月次・1.8.0 は 2026-09-04 公開）。停止した旧名を避けたのも同じ実測による。
- 決定 3: **代償を先に測って記録に残す**ためである。数字を残さないと、次に初期ロードを増やす作業が
  「何が 12% を占めているか」を知らずに床を上げる。

## 結果

- 良い影響:
  - 三部品と区画部品が 1 か所にあり、17 画面がそれを引くだけになった（IADR-0437 と対）。
  - 確認ダイアログのフォーカストラップ・Esc・復帰が**ライブラリの責務**になった（自前実装の削除）。
  - Storybook（`Nocturne.stories`）と単体テスト 95 件で見た目と役割を固定した。
- 悪い影響・トレードオフ:
  - 🔴 **初期ロードの約 12%（114 kB）が Base UI である。** 統合後の床 705,386 B に対する比である。
  - 土台が 2 つ（Base UI ＋ Radix Tabs）になった（決定 4）。
- フォローアップ:
  - **初期ロードを次に増やす作業は、先に Base UI の切り離しを検討すること。** 選択肢は
    (a) 右レールから `Tooltip` を外す、(b) `manualChunks` を `@base-ui` のサブパスで分ける
    （`vendor-baseui-dialog`）——どちらでも 85 kB 前後を遅延側へ動かせる見込みである（未実測）。
  - `@platform/ui` にネイティブ要素を包むプリミティブを足したら、`eslint.config.js` の
    `jsx-a11y` の `components` 対応表へも足すこと（IADR-0440 決定 2）。

## 関連

- Supersedes: なし。
- Superseded by: なし。
- **補完する**: [IADR-0125](IADR-0125_ui-primitives-i18n-catalog-and-storybook.md)。
  同決定 1 の**規則**（3 情報源の突き合わせ・公開面 1 ファイル・文言を持たない）は**そのまま生きている**。
  本決定が変えたのは決定 2「Dialog は移植しない」の**前提**だけ——当時それを要求していた画面が
  着手保留にあったのに対し、保留は解け、確認ダイアログと右レールが現に要求している。
  **規則ではなく実値が動いたので supersede ではない**（IADR-0125 §関連 が
  「実値を埋める部分改定」と呼んでいるのと同じ型である）。被補完側 IADR-0125 の §関連 へ
  本 ID を併記した。
