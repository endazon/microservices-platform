---
title: submodule src/ai-stock-trading を AST#793（文言カタログの遅延登録）と AST#794 取り込み後の develop へ進め、合成ビルドのチャンク床を実測で下げる
type: spec
status: done
related_ids: [NFR, FR-14, ADR-0031, IADR-0120, IADR-0134, IADR-0441, IADR-0443]
author: Claude
created: 2026-09-12
updated: 2026-09-12
plan_refs:
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
---

# 仕様書: AST submodule の前進（AST#793 の取り込み）とチャンク床の引き下げ

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: `FR-14`（可変ユニットの合成）
- 非機能要件（NFR）: 無採番（submodule の前進・床の更新はメタ作業。`.claude/rules/traceability.md`
  §起点 ID の種別 の場合 2。環流しない。先行の `20260912_ast-submodule-bump-ui-ux.md` と同じ扱い）
- ユースケース（UC）: なし
- 画面（SC）: `AST/SC-01`〜`AST/SC-04`（AST 側の 4 画面。本リポジトリの画面ではない）
- 関連 ADR: `ADR-0031`（フロントエンドスタック）/ `IADR-0120`（AST は本リポジトリから変更しない）/
  `IADR-0134`（初期ロードの ratchet）/ `IADR-0441`（AST の文言登録と合成点の任意項目読み。決定 3 の追記）/
  `IADR-0443`（Base UI の初期ロード外し。本作業の土台となる床 606,019 を作った）
- 計画書リンク: `projects/microservices-platform/06_technical/13_frontend-stack.md`

## 目的・背景

#1439 で submodule を `7780edc` へ進めた際の +25,907 B は、AST の ja カタログ（442 文言・25,267 B）が
合成点の同期 `registerUnitMessages` 経由で初期チャンクへ入ったものであった（#1440 の訂正）。
AST#793（endazon/ai-stock-trading#793。AST/IADR-0340）がカタログの登録を 4 画面の Page（遅延チャンク）側へ
移し、`features/index.ts` からの `aiStockTradingMessages` 再公開を外した。develop `c5cd0de` に入った同 PR を
取り込み、下がった初期ロードを床へ反映する（ratchet は下げる方向にも実測で追随させる。IADR-0134）。

🔴 **`c5cd0de` を取り込んだ最初の合成 `test:coverage` で AST の `catalogRegistration.test.ts` が 1 件落ちた**
（1 failed / 1623 passed。母集合テストが `process.cwd()` から実ソースを解決しており、cwd = `src/` の合成実行では
ENOENT）。AST 単独の CI では見えない破れである。基盤からは直せない（IADR-0120）ので AST#794
（endazon/ai-stock-trading#794）で是正し、develop `8a0b3e1` に入った。**本作業はこの SHA を取り込む**
（`c5cd0de` では合成 CI が赤になる）。

**submodule を前進させる自動化は無い**（#1439 で実測済み。`.github/workflows/` に submodule bump の PR を
作る仕組みは無く、automation/* は changelog / openapi だけ）。手動 PR が要る。

## 対象範囲

- 対象:
  - `src/ai-stock-trading` を develop `7780edc` → `8a0b3e1`（AST#793 と AST#794 の squash マージ 2 件）へ
  - `scripts/chunk-budget-baseline.json` の `initialTotalBytes`（`--update`）と、減分の内訳を記す
    `$comment_initialTotalBytes_20260912_ast-bump-793`
- 対象外:
  - `src/pnpm-lock.yaml`（AST#793 は依存を足しておらず `pnpm install` で差分が出ない。実測）
  - 合成点 `platform/frontend/src/features/index.ts` の任意項目読み（`astOptionalSurface`）。IADR-0441
    決定 3 の追記どおり**据え置く**。`aiStockTradingMessages` が公開されなくなったので登録は skip され、
    `aiStockTradingBreadcrumbs` は引き続き読む
  - `breadcrumbs.test.ts`（AST のパンくず公開は不変。#1439 の是正のまま緑）
  - コードの変更は無い（IADR-0120。AST は本リポジトリから変更しない）

## 設計

1. designated branch を develop `6fc4585`（#1440 マージ後）から作り直す（旧ブランチは #1440 でマージ済み）。
2. submodule を `origin/develop`（`8a0b3e1`）へ checkout し、`pnpm install`（差分なしを確認）。
3. `rm -rf platform/frontend/dist` → `pnpm run build` → `node scripts/check-chunk-budget.js`（床超過なし・
   実測が床を下回ることを確認）→ `--update` で床を下げる。**古い dist を測らない**（#1438 の教訓）。
4. 減分の内訳（動いたチャンク・カタログの合流先）を `$comment` へ書く。
5. 母集合（規則 1〜10）: 「submodule の SHA を書く文書」を `7780edc` で走査した →
   `.ai-context/adr/IADR-0441` §追記・`scripts/chunk-budget-baseline.json` の `$comment_…_ast-bump`・
   `.ai-context/specs/20260912_ast-submodule-bump-ui-ux.md`・`20260912_1437_vendor-baseui-split.md`
   （着手前の実測の前提）は、いずれも**その時点の観察記録**（`7780edc` 時点の実測）であり書き換え対象では
   ない。今回の SHA は本仕様書と新しい `$comment` が持つ。除外: `CHANGELOG.md`（生成物）。

## 受け入れ基準

- [x] `git -C src/ai-stock-trading rev-parse HEAD` が `8a0b3e1e…`（AST develop の HEAD。AST#794 を含む）
- [x] `pnpm install` が差分なしで完了する
- [x] `pnpm run build` OK・`check-chunk-budget --require` OK（床 = 実測 580,710 B。`requiredChunks` 8 本実在）
- [x] 初期ロードで動いたのは `index-*.js` の 1 本だけで、`ui` / `vendor-react` / `vendor-query` は不変
- [x] AST の ja カタログが初期ロード（`index.html` の modulepreload / エントリ）に含まれない
- [x] typecheck / lint 0 error / format / knip 床どおり / i18n 差分なし・未訳 0 / route-manifest /
  static-egress / test:coverage（AST の 404 件を含む）/ E2E 緑（§検証記録）

## テスト方針

新規テストは書かない（コードの変更が無い）。合成ビルドの実測（chunk）と既存の単体・E2E が検証である。
AST 側の回帰ガード（`catalogRegistration.test.ts` 7 件）は `src/vitest.config.ts` の `include` により本リポジトリの
`test:coverage` で横断実行される。

## 検証記録

### チャンク（`rm -rf dist` → `pnpm run build` → `check-chunk-budget`。1000 進）

| 項目 | develop（AST `7780edc`・床） | 本作業（AST `8a0b3e1`） | 差 |
| --- | --- | --- | --- |
| 初期ロード合計 | 606,019 B | **580,710 B** | **−25,309 B（−4.2%）** |
| `index-*.js` | 287,014 B | 261,705 B | −25,309 B |
| `ui` / `vendor-react` / `vendor-query` | 76,575 / 196,828 / 45,602 B | 同値 | 0 |
| 最大チャンク | 586.04 kB（vendor-echarts） | 586.04 kB | 0 |
| 1 kB 未満の遅延チャンク | 10 本 | 10 本 | 0 |
| `queries-*.js`（AST 4 画面共有の遅延チャンク） | 9,641 B | 35,000 B | +25,359 B |

（A/B: 同一環境で submodule を `7780edc` へ戻して `rm -rf dist` → 再ビルドし、`index.html` が読む 4 本を突き合わせた。
`ui` / `vendor-react` / `vendor-query` はファイル名のハッシュまで同一。）

- AST の ja カタログの合流先: AST 4 画面が共有する既存の遅延チャンク `queries-*.js`（35,000 B）。
  `index.html` は読まない（grep で AST 固有文言が初期ロードの 4 本に無いことを確認。`index-*.js` に残る
  「株式自動売買」は左ナビのグループ名であり、計画どおり共通シェルの文言である）。
- AST 側の単独実測（731,435 → 706,121 = −25,314 B）との差 5 B は、基盤側が #1440（Base UI 外し）後の
  成果物を土台にしていることによる（同一チャンクのハッシュ長・順序の違い）。

### ゲート

§受け入れ基準の最終項目の実測は PR 本文に転記する。`c5cd0de` 時点の実測は上記 1 件（AST#794 で是正）を除き緑で、
`8a0b3e1`（差分は当該テストファイルと `.ai-context/` のみ。成果物は同一）で `test:coverage` を再走した。

## 計画書との差異

- 差異: なし

## 未決事項

- なし
