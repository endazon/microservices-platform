---
title: "知識グラフの frontmatter 読み取りをフロー形式の YAML へ広げ、参照 3,567 件を検査下へ入れる（#1462）"
type: spec
status: done
related_ids: [IADR-0452]
author: Claude
created: 2026-09-13
updated: 2026-09-13
plan_refs: []
---

# 仕様書: 知識グラフがフロー形式の `related_ids` を読めるようにする

> 本作業は**メタ作業（検査器の欠陥の是正）**であり、計画書由来の起点 ID を持たない。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）/ ユースケース（UC）/ 画面（SC）: なし
- 非機能要件（NFR）: **無採番**（planning#311 の裁定。工程の管理は製品の非機能要件表に番号を持たない。**環流しない**）
- 起票: #1462（#1460 の実測から切り出した）
- 関連 IADR: IADR-0452（`related_ids` の値域。同じ「YAML の 2 形式」の穴を検査側で塞いだ記録）

## 目的・背景

`scripts/gen-knowledge-graph.js` の `yamlListField()` は **YAML のブロック形式しか読まない**。

```js
const re = new RegExp(`^${key}:\s*\n((?:^[ \t]*-[ \t]*.+\n?)*)`, 'm');
```

したがって `related_ids: [FR-19, IADR-0451]` のような**フロー形式は 1 件もエッジにならず**、`--check`（in-repo のエッジ先が実在するか）も**その範囲を検査していない**。

実測（`develop` = `fbc8099`）:

| 形式 | 欄の数 |
| --- | --- |
| ブロック形式 | 1,399 |
| **フロー形式** | **323** |

**エッジになっていない参照は 3,567 件**（322 ファイル）。エッジ総数 11,586 に対し**約 23% 相当が欠落**しており、`related_ids` / `related_adrs` / `related_specs` の 3 キーすべてで同じ穴がある。

**#1460 の受け入れ基準を実測して見つかった** —— 自己参照 9 件を落としても辺が 1 本しか減らず、8 件（フロー形式）は**そもそもエッジになっていなかった**。

## 対象範囲

- 対象: `scripts/gen-knowledge-graph.js` の `yamlListField()`、その `--self-test`、`scripts/scripts.repo.test.js`
- 対象外: **参照切れの是正**（出れば別 issue）、`docs/` の trace ブロック（`lib/trace-blocks.js` が別途読む）、計画リポジトリ側の参考実装（本リポジトリは planning に依存しない）

## 母集合（規則 1〜10）

**誤りの側から引いた**（規則 1）——「フロー形式で書かれた `related_*` の欄」を `.ai-context/{adr,specs}` の全件で走査した。

| 軸 | 結果 | 扱い |
| --- | --- | --- |
| `related_ids` / `related_adrs` / `related_specs` のフロー形式 | **323 欄・3,567 参照**（322 ファイル） | **対象**（本修正で読めるようになる） |
| 同・ブロック形式 | 1,399 欄 | 対象外（既に読めている。**退行させないことをテストで固定する**） |
| `docs/` の trace ブロック | `lib/trace-blocks.js` が読んでおり本欄とは別経路 | 対象外 |
| 同じ `yamlListField` を使う他のキー | 3 キーのみ（`edgesFromFrontmatter` で確認） | **対象**（キーを区別せず直る） |

**自己参照（規則 8）**: 本仕様書自身も `related_ids` を持つが、走査は形式の分類であり母集合の数は本書の追加で 1 欄増える（`323 → 324`）。**上の数は本書を書く前の実測値**である。

## 設計

`yamlListField()` に**フロー形式の分岐を前置**する。**ブロック形式の読み方は変えない**（退行させない）。

```js
const flow = new RegExp(`^${key}:[ \t]*\[([^\]]*)\]`, 'm').exec(fm);
if (flow) { /* カンマ区切り・引用符落とし */ }
```

- **フロー形式を先に見る。** 同じキーが両形式で書かれることは無い（YAML として不正）。
- **`[...]` の中に `]` を含めない**（`[^\]]*`）。ID の列挙に閉じ括弧は現れない。
- **空配列 `[]` は空の並び**として返す（`filter(Boolean)` が落とす）。
- 引用符の除去はブロック形式と同じ処理に揃える。

🔴 **参照切れが出るかどうかを先に実測した。** 検査を効かせてから考えるのでは範囲が読めないためである。実装前に検査器の写しへ同じ修正を当てて走らせ、**エッジ 11,586 → 15,153（+3,567）で `--check` は緑**、`related_specs` の unresolved は従来どおり 1 件（submodule 配下の fail-open）だけであることを確かめた。**したがって参照切れの是正は本作業には発生しない**（#1462 が「出たら別 issue へ」と書いた分岐は空振りである）。

**最終の実測**（本仕様書を含む作業ツリー）: **ノード 1,360 / エッジ 15,154**。`develop`（`fbc8099`）は 1,359 / 11,586 なので **+3,568** で、内訳は**パーサの是正 +3,567** と**本仕様書自身が持つ `related_ids` の 1 本**である。

## 受け入れ基準（#1462 の写像）

- [x] フロー形式の `related_ids` がエッジとして現れる
- [x] 両形式が読めることが `--self-test` と `scripts.repo.test.js` で固定されている（**片方しか読まない実装で落ちる**）
- [x] `gen-knowledge-graph --check` が緑
- [x] エッジ数の増分が実測として記録されている（**11,586 → 15,154 / +3,568。うちパーサ由来 +3,567**）
- [x] ブロック形式の読み取りが退行していない（正例を残す）

## テスト方針

`gen-knowledge-graph.js --self-test` にフロー形式の正例・境界（空配列・引用符・次のキーを食べない）を足し、`scripts.repo.test.js` は既存の「`--self-test` が exit 0」「`--json` の形」を通じて CI から強制する（**新しい実行経路を増やさない**）。

さらに **実データの検査を 1 本足す** —— 自己試験の正例だけでは、**パーサが片方の書き方しか読まなくても実データ側は「エッジが 0 でない」で緑になる**（ブロック形式が多数派のため）。「フロー形式で `related_ids` を書いた実装ADR が 1 本もエッジを持たない」ことを落とす形にし、走査 0 件で緑を返さない門も置く。

**変異試験で効き目を確かめた**: `yamlListField` のフロー分岐を潰す（`const flow = null`）と自己試験の
`yamlListField: 正例 — フロー形式 [a, b] を読む（#1462）` が **FAIL** し、`scripts.test.js` 全体が落ちる。
是正を戻すと **782 tests 緑**。

## 検証（`/verify` 相当）

`node scripts/gen-knowledge-graph.js --self-test` / `--check` / `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` / `node scripts/check-doc-links.js` / `node scripts/check-adr-numbering.js` / `node scripts/check-trace-blocks.js` / `node scripts/check-doc-type-vocabulary.js` / `node scripts/check-doc-status-vocabulary.js` / `node scripts/check-plan-id-qualification.js` / `node scripts/check-cross-repo-refs.js` / `node scripts/check-commit-messages.js --range origin/develop..HEAD`。

**フロントの検査は走らせない**（差分が `src/` に触れない）。
