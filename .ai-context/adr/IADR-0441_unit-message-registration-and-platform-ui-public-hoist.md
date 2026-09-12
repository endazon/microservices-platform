---
title: IADR-0441 可変ユニットの文言カタログを合成点で束ねる登録口と、共有 UI の公開 hoist
type: impl-adr
status: Accepted
related_ids: [FR-14, NFR, ADR-0031, IADR-0056, IADR-0120, IADR-0121, IADR-0124, IADR-0125, IADR-0436]
author: Claude
created: 2026-09-12
updated: 2026-09-12
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
  - planning:projects/microservices-platform/06_technical/10_composability-design.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
related_specs:
  - ../specs/20260912_frontend-nocturne-and-experience-gaps.md
---

# IADR-0441: 可変ユニットの文言カタログを合成点で束ねる登録口と、共有 UI の公開 hoist

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-09-12
- 決定者: Claude（実装）／英訳を持たせない方針は利用者裁定（2026-09-12 裁定 3）

## 起点・関連

- 関連する計画書 ID:
  FR-14（可変機能ユニットの合成）／10_composability-design（計画リポ）／
  13_frontend-stack（計画リポ）（i18n = Lingui〔ja / en〕）／
  ADR-0031（計画リポ）／05_screens（計画リポ）§共通シェル（ユニットの左ナビグループ・パンくず）
- 関連する実装 ADR:
  [IADR-0124](IADR-0124_tanstack-router-unit-composition.md)（**決定 1 = 合成点がユニットを知る唯一の場所**。本決定はこの原則の 3 例目〔ルート・ナビ・パンくずに次ぐ文言〕）／
  [IADR-0125](IADR-0125_ui-primitives-i18n-catalog-and-storybook.md)（決定 3 = コンパイル済みカタログの import／決定 4 = 未翻訳キーの検出／決定 9 = ユニットの左ナビグループ）／
  [IADR-0120](IADR-0120_excluded-units-from-gitmodules.md)（**submodule ユニットは別プロジェクトである。本決定はこれを維持する**）／
  [IADR-0121](IADR-0121_spa-stack-migration-staging.md)（決定 2 = pnpm workspace・決定 4 = `@platform/ui`）／
  [IADR-0056](IADR-0056_repo-unit-structure-platform-knowledge.md)（ユニット構成）／
  [IADR-0436](IADR-0436_shared-ui-states-panels-and-base-ui-adoption.md)（hoist の対象になる共有 UI）
- 関連する実装仕様書:
  [20260912_frontend-nocturne-and-experience-gaps](../specs/20260912_frontend-nocturne-and-experience-gaps.md)
- 関連 issue: 本 PR（UI/UX 改善）。対になる作業は当該ユニット側の同日 PR（`AST#...`。番号は起票時に確定）

## コンテキストと課題

別プロジェクトの可変機能ユニット（submodule で取り込む）が、本リポジトリの共有 UI（`@platform/ui`）と
Lingui を使うことになった。**そのユニットは本リポジトリの計画に属さない**（IADR-0120）ため、
ユニット側は自分のリポジトリだけで完結してビルド・テストできる必要がある。

3 つの問題が出た。

1. **文言カタログを基盤の i18n へどう束ねるか。** 基盤の `lib/i18n` が当該ユニットを import する形は
   ESLint（`no-restricted-imports`）が禁じている —— **foundation が可変ユニットを知ってはならない。**
2. **英訳を持たないユニットをどう扱うか。** 裁定 3 は「Lingui を導入するが英訳は不要」（ja のみ）と定めた。
   何も流さないと、本番ビルド（`msg` マクロが `message` を落とし ID だけを残す）で
   **en ロケールの当該画面にハッシュがそのまま出る。**
3. 🔴 **合成時に当該ユニットから `@platform/ui` が解決できない。** pnpm は workspace パッケージを
   **宣言した importer にしか link しない**。当該ユニットは別リポジトリであり
   `workspace:*` を宣言できない（npm が `EUNSUPPORTEDPROTOCOL` で拒否する。実測）。

加えて、**submodule のポインタ前進と本リポジトリの合成点の変更は、どちらが先に着地するか分からない。**
名前付き import にすると、順序によって `tsc` が落ちる。

## 検討した選択肢

| 論点 | 案 | 却下理由 |
| --- | --- | --- |
| 文言の束ね | **合成点が `registerUnitMessages` を呼ぶ（採用）** | — |
| | `lib/i18n` がユニットを import | **foundation が可変ユニットを知る**。ESLint が禁じている |
| | ユニットが `i18n.load` を直接呼ぶ | 基盤の i18n インスタンスをユニットへ露出する。読み込み順序も制御できない |
| 未提供ロケール | **未登録 ID に限って提供ロケールを流す（採用）** | — |
| | 無条件に流す | 🔴 **基盤の英訳を日本語で上書きする**。ID はメッセージ本文のハッシュなので、同じ本文（例「保存」）は同じ ID になる |
| | 何も流さない | en で**ハッシュがそのまま表示される** |
| ユニットの公開面 | **名前空間 import から任意項目として読む（採用）** | — |
| | 名前付き import | **submodule bump との順序依存**。旧ユニットで `tsc` が落ちる |
| 共有 UI の解決 | **`publicHoistPattern: ['@platform/ui']`（採用）** | — |
| | ユニットに `workspace:*` を宣言させる | **できない**（npm が拒否。実測） |
| | 共有 UI を npm パッケージとして公開 | 私的パッケージであり配布経路が無い。IADR-0121 決定 4 の前提を変える |
| | `publicHoistPattern: ['*']` | knip・依存方向の検査が**まとめて緩む** |

## 決定

### 決定 1: 文言は合成点（`features/index.ts`）だけが束ねる。基盤は `registerUnitMessages` を公開する

合成点はルート・ナビ・パンくずと同じく**ユニットを知る唯一の場所**である（IADR-0124 決定 1）。
基盤（`lib/i18n`）は「カタログを受け取る口」だけを公開し、誰のカタログかを知らない。

- 与えられたロケールは `i18n.load(locale, messages)` で**追加**する。`@lingui/core` の `load` は
  既存カタログへ `Object.assign` でマージする（実測: `_load` が `Object.assign(maybeMessages, messages)`）
  ので、**基盤のカタログは消えない。**
- ロード後に `activate` し直す必要は無い（`i18n._` は呼び出し時点の表を引く）。
- 順序: `@foundation/i18n` はモジュール読み込み時に基盤のカタログを load / activate 済みであり、
  ここでの追加ロードはその後に走る（`main.tsx` の `initI18n()` はさらに後。**load はいつ行っても効く**）。

### 決定 2: 未提供ロケールには、**そのロケールに未登録の ID に限って**提供ロケールの文言を流す

裁定 3 のとおり当該ユニットは ja しか持たない。ja を en へ流せば
「当該ユニットの画面は en でも日本語で出る」という裁定どおりの見え方になる。

🔴 **流すのは未登録の ID だけである。** ID はメッセージ本文のハッシュ（`msg` マクロが両者で同じ算法）なので、
ユニットの ja と基盤の en が**同じ本文**（例「保存」）を持つと ID が一致する。
無条件に流すと**基盤の英訳が日本語で上書きされる**。テストでこれを固定する
（「en には未登録 ID だけ ja が流れ、既存訳は維持される」）。

### 決定 3: ユニットの新しい公開面は**任意項目**として読む（順序非依存）

合成点は名前空間 import から `aiStockTradingMessages` / `aiStockTradingBreadcrumbs` を
任意項目として取り出す。**submodule のポインタ前進より先に本変更が着地しても `tsc` が落ちず、
bump が来た時点で自然に有効になる。**

**bump 後に名前付き import へ戻してよい。** 任意項目は順序依存を消すための一時的な形であり、
恒久的な作法ではない（型が緩むので、戻せるときに戻す）。

> ［2026-09-12 追記 / #1437 / AST#792］**決定 3 の任意項目読みは bump 後も維持する（名前付き import へ戻さない）。**
> AST#793（endazon/ai-stock-trading#793。AST/IADR-0340）で、当該ユニットは文言カタログの登録を**画面の遅延チャンク側**
> （`src/lib/i18n.ts` のモジュール評価時に `registerUnitMessages` を自ら呼ぶ）へ移し、`features/index.ts` からの
> `aiStockTradingMessages` の再公開を外した。名前付き import へ戻すと基盤の tsc が落ちる。
> 決定 1「合成点だけが束ねる」は**登録口の所有**（`registerUnitMessages` は基盤が公開し、foundation は
> ユニットを知らない）の意味で維持し、**呼び出し元がユニットの遅延チャンクであることを許す**——
> 合成点で同期に呼ぶと ja カタログ 25,267 B（442 文言）が初期チャンクへ入る（MSP#1439 の +25.9 kB の 97.5%。
> 実測は AST#792 のコメントと `scripts/chunk-budget-baseline.json` の `$comment_…_ast-bump` の訂正を参照）。

### 決定 4: `publicHoistPattern` は `@platform/ui` **ただ 1 つ**に限る

ルート `node_modules` へ公開 hoist し、当該ユニットが knowledge と同じ経路で解決できるようにする。
**対象を広げない** —— 広げると knip（未使用依存の検出）と依存方向の検査がまとめて緩む。

### 決定 5: IADR-0120 の線引き（submodule ユニットは別プロジェクト）は**維持する**

本決定は「本リポジトリの検査器が当該ユニットを検査対象にする」ものではない。
変えたのは**解決経路**（hoist）と**登録口**（`registerUnitMessages`）だけである。
Lingui の抽出設定（`src/lingui.config.ts`）も当該ユニットを含めない —— ユニットは
**自分のカタログを自分で生成し、生成物を公開面から渡す。**

## 理由

- 決定 1 は既存の原則（合成点がユニットを知る唯一の場所）の 4 例目にすぎない。
  新しい機構ではなく、**同じ形を文言にも適用しただけ**である。
- 決定 2 の「未登録 ID に限る」は、**ID がハッシュである**という Lingui の性質から必然的に出る制約である。
  性質を知らずに書くと静かに英訳が壊れる。
- 決定 3 は**マージ順序を人が調整しなくて済む**ようにするためである。順序依存は「どちらを先に
  マージしても赤」という形で現れ、FIFO のマージ運用と相性が悪い。
- 決定 4 の「1 つだけ」は、**hoist が検査を緩める副作用**を最小化するための境界である。

## 結果

- 良い影響:
  - 当該ユニットが共有 UI・トークン・アイコンを使えるようになり、見た目の断絶が消える。
  - 本リポジトリと当該ユニットの PR を**どちらの順でもマージできる**。
- 悪い影響・トレードオフ:
  - `publicHoistPattern` は**ルート `node_modules` を変える**。宣言していないパッケージからも
    `@platform/ui` が import できてしまう（依存方向の ESLint は効くが、pnpm の隔離は弱まる）。
  - **任意項目読みは型が緩い**（`as unknown as {...}`）。bump 後に戻すまでの間、
    公開面の改名を型が捕まえない。
  - 🔴 **`pnpm-lock.yaml` は submodule の bump で動く。** 本 PR には submodule のポインタ前進を
    含めないため、bump PR で lockfile の差分が出る。
- フォローアップ:
  - ~~submodule bump 後に、合成点の任意項目読みを**名前付き import へ戻す**（決定 3）。~~ ［2026-09-12 追記 / #1437］**撤回**。上の決定 3 の追記のとおり維持する。

## 関連

- Supersedes: なし。
- Superseded by: なし。
