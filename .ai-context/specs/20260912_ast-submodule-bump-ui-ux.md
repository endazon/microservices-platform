---
title: submodule src/ai-stock-trading を UI/UX 改善後の develop へ進め、合成ビルドのチャンク床を実測で更新する
type: spec
status: done
related_ids: [NFR, FR-14, ADR-0031, IADR-0120, IADR-0134, IADR-0436, IADR-0441]
author: Claude
created: 2026-09-12
updated: 2026-09-12
plan_refs:
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
---

# 仕様書: AST submodule の前進（UI/UX 改善の合成）とチャンク床の更新

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: `FR-14`（可変ユニットの合成）
- 非機能要件（NFR）: 無採番（submodule の前進・床の更新はメタ作業。`.claude/rules/traceability.md`
  §起点 ID の種別 の場合 2。環流しない）
- ユースケース（UC）: なし
- 画面（SC）: `AST/SC-01`〜`AST/SC-04`（AST 側の 4 画面。本リポジトリの画面ではない）
- 関連 ADR: `ADR-0031`（フロントエンドスタック）/ `IADR-0120`（AST は本リポジトリから変更しない）/
  `IADR-0134`（初期ロードの ratchet）/ `IADR-0436`（Base UI）/ `IADR-0441`（AST の文言登録と公開 hoist）
- 計画書リンク: `projects/microservices-platform/06_technical/13_frontend-stack.md`

## 目的・背景

#1436（UI/UX 改善。IADR-0435〜0441）は submodule `src/ai-stock-trading` の SHA 前進を含めず、
「AST#791 マージ後の自動 PR に委ねる」としていた。**その自動化は存在しない**（`.github/workflows/` を
`submodule|bump|repository_dispatch` で走査。automation/* の PR は changelog / openapi だけ）。
AST#791（endazon/ai-stock-trading#791。AST の 4 画面を `@platform/ui` と Lingui へ載せ替え）は
2026-09-11 23:40Z に develop へ入った（`7780edc`）。本作業でこれを取り込み、合成ビルドの
初期ロードが床を超える分を実測で床へ反映する（#1437 の作業 1・2）。

## 対象範囲

- 対象:
  - `src/ai-stock-trading` を develop `3d35c7a` → `7780edc` へ
  - `src/pnpm-lock.yaml`（AST の依存追加: `@lingui/core` / `@lingui/react` / `lucide-react` は基盤と
    同一版に解決される。`pnpm install` で再生成）
  - `scripts/chunk-budget-baseline.json` の `initialTotalBytes` / `smallLazyChunks`（`--update`）と、
    増分の内訳を記す `$comment`
  - `platform/frontend/src/app/routing/breadcrumbs.test.ts` の 1 テスト（計画の 17 画面とパンくず宣言の
    完全一致）。AST が `aiStockTradingBreadcrumbs` を公開したため登録は 21 件になり、**「計画の全画面に
    宣言がある」という意図を保ったまま**、ユニットの左ナビが持つパスを引いてから突き合わせる形へ
    （合成点の外で AST を import しない。`unitNavGroups()` から引く）。陰性対照（計画外のパスは
    ユニットのナビに限る）を足す
- 対象外:
  - AST 画面の lazy 化（`lazyRouteComponent`）。**AST は本リポジトリから変更しない**（IADR-0120）。
    AST 側へ issue を起票して委ねる（後述）
  - `vendor-baseui` の切り離し（#1437 の作業 4。本作業で初期ロードは増えるが Base UI の量は不変）
  - `@ai-stock-trading` の合成点シム（`features/index.ts` の任意項目読み）の名前付き import への戻し。
    IADR-0441 は「bump 後に戻してよい」としたが、**戻す義務は無く**、戻すと AST の次の公開面追加で
    再び順序依存が生まれる。据え置く

## 設計

1. submodule を `origin/develop`（`7780edc`）へ checkout し、`pnpm install` で lockfile を再生成する。
   `@platform/ui` は `publicHoistPattern`（IADR-0441 決定 2）により root の `node_modules` から解決される。
2. `pnpm run build` → `node scripts/check-chunk-budget.js` で超過を確認 → `--update` で床を進める。
3. 増分の内訳（A/B）を `$comment_initialTotalBytes_20260912_ast-bump` へ書く。
4. 母集合（規則 1〜10）: 「submodule の SHA を書く文書」を `3d35c7a` / `0844b584`（旧 pin）で走査した
   → `.github/workflows/claude-coding.yml:167` のコメントが旧 pin `0844b584` の root tree を説明している
   が、これは**その時点の観察記録**であり（pin に `.gitmodules` が無いことは今も同じ）、書き換え対象
   ではない。他に SHA を持つ文書は無い（IADR / spec は「develop」と書く）。除外なし。

## 受け入れ基準

- [x] `git -C src/ai-stock-trading rev-parse HEAD` が `7780edc5…`（AST develop の HEAD）
- [x] `pnpm install` が lockfile 差分だけで完了し、`pnpm -r run typecheck` が 6 パッケージ Done
- [x] `pnpm run build` OK・`check-chunk-budget --require` OK（床 = 実測。`requiredChunks` 7 本実在）
- [x] `pnpm run test:coverage` が AST の 397 件を含めて緑で、床（93/93/89/88）を割らない（初回は
  `breadcrumbs.test.ts` の 1 件が 17≠21 で赤 → 上記のとおり是正して緑）
- [x] lint 0 error / format / knip 床どおり / i18n 差分なし・未訳 0 / route-manifest / static-egress OK
- [x] E2E（Playwright）緑。合成後の左ナビに「株式自動売買」グループと AST 4 画面が出る
- [x] AST 画面の lazy 化を AST 側へ issue で委ねた（AST#792）

## テスト方針

新規テストは書かない（コードの変更が無い）。合成ビルドの実測（chunk・E2E）が検証である。
AST の単体テスト 397 件は `src/vitest.config.ts` の `include` により本リポジトリの `test:coverage` で
横断実行される（`@platform/ui` は実体へ解決され、AST の `test/ui-stub` は使われない）。

## 検証記録

### チャンク（`pnpm run build` → `check-chunk-budget`。1000 進）

| 項目 | develop（AST `3d35c7a`） | 本作業（AST `7780edc`） | 差 |
| --- | --- | --- | --- |
| 初期ロード合計 | 705.39 kB（床 705,386 B） | 731.29 kB | +25.91 kB |
| 最大チャンク | 586.04 kB（vendor-echarts） | 586.04 kB | 0 |
| 1 kB 未満の遅延チャンク | 9 本 | 10 本 | +1 |

増分の内訳（A/B）:
- **A. AST の 4 画面本体**: AST の route factory は `component:` へ画面をそのまま渡しており
  （`lazyRouteComponent` を使う knowledge の 17 画面と違う）、**合成点の静的 import 経由で初期チャンク
  （`index-*.js`）へ入る**。これが増分の大半である。
- **B. AST の ja カタログ（442 文言）と lucide のアイコン追加分**: `registerUnitMessages` で合成点が
  読むため初期ロードに載る（基盤のカタログと同じ扱い）。lucide は基盤と同一版へ解決され、AST が
  新たに使うアイコンだけが増える。
- `index.esm-*.js`（79.5 kB）は **AST とは無関係**である: `@hookform/devtools` が持ち込む
  `little-state-machine` で、`AnalysisDashboardPage`（SC-10・遅延チャンク）だけが import する。
  初期ロードには入らない。#1437 の作業 2 は**これで解消**（正体の確認のみで規則は不要）。
- 1 kB 未満の遅延チャンク +1 本: AST の画面が `@platform/ui` を使うようになり、AST 画面と knowledge の
  遅延画面が共有する小モジュールが 1 本増えた（往復が 1 回増える程度。規則の追加はしない）。

### 次の一手（AST 側。本リポジトリでは直せない）

AST の 4 画面を `lazyRouteComponent` へ載せ替えれば A の分は遅延チャンクへ移る。
knowledge と同じ形にする作業は AST#792 へ委ねる。

## 計画書との差異

- 差異: なし

## 未決事項

- なし

## 追記

［2026-09-12 追記 / #1437 / AST#792］§検証記録の「増分の内訳 A/B」は誤りであった。AST の 4 画面は #723/#691 から
`lazyRouteComponent` 方式で、合成 dist でも独立した遅延チャンク（計 66,182 B）＝初期ロードへの寄与は 0 B。
+25,907 B の 97.5% は ja カタログ 25,267 B（合成点が同期に `registerUnitMessages` で読み、`index-*.js` に入る）。
AST#792 はこの訂正を受けて「カタログ登録を画面の遅延チャンク側へ移す」作業に変わり、AST#793 で実装された
（AST 側実測 731,435 → 706,121 B）。`scripts/chunk-budget-baseline.json` の `$comment` に同じ訂正を書いた。

［2026-09-12 追記 / AST#793 取り込み］AST#793 は develop `c5cd0de` に入り、本リポジトリは submodule を `8a0b3e1`（AST#794 を含む）へ進めて床を
606,019 → 580,710 B（−25,309 B）へ下げた。実測と手順は `.ai-context/specs/20260912_ast-submodule-bump-793.md`。
