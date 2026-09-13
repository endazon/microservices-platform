---
title: "実装ADR 索引の状態列を本体 status: へ戻し、語彙と一致の機械検査を置く（#1459）"
type: spec
status: done
related_ids: [IADR-0144, IADR-0447, IADR-0448, IADR-0449, IADR-0450]
author: Claude
created: 2026-09-13
updated: 2026-09-13
plan_refs: []
---

# 仕様書: 実装ADR 索引の状態列の是正と機械検査

> 本仕様書は実装着手前に作成する。本作業は**メタ作業（文書統制・検査器の追加）**であり、
> 計画書由来の起点 ID を持たない（`.claude/rules/traceability.md`「起点 ID の種別」の 2 番目の場合）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（メタ作業）
- 非機能要件（NFR）: **無採番**。計画側の非機能要件は稼働する製品の要件であり、工程の管理は別の軸である（planning#311 の裁定。**環流しない**）
- ユースケース（UC）/ 画面（SC）: なし
- 関連 IADR: [[IADR-0144]]（索引行の抽出式は 1 つに畳む）
- 起票: #1459

## 目的・背景

`.ai-context/adr/README.md` の一覧は `| IADR | タイトル | 状態 |` の 3 列だが、**直近 4 行の 3 列目に状態ではなく日付**が入っている。索引から「その決定が生きているのか」が読めない。

**2 列目（タイトル）は検査され、3 列目（状態）は素通りである**：

| 検査 | 見ているもの |
| --- | --- |
| `check-adr-numbering.js` | 採番の重複・欠番、索引⇄本体の双方向一致（**ID だけ**） |
| `scripts.repo.test.js` の `inspectAdrIndexTitles` | **タイトルセル**（空・状態語の混入・追記・200 字上限・本体 title との LCS） |
| （無し） | **状態セル** |

同型の混入が **4 回**起きており、規約の条件（同型の事故が 2 回起きたら検査器を足す）を満たす。

## 対象範囲

- 対象: `.ai-context/adr/README.md` の状態セル 4 行の是正、`scripts/scripts.repo.test.js` への検査追加
- 対象外: タイトルセルの検査（既存の `inspectAdrIndexTitles` に手を入れない）、計画 ADR の索引、`.ai-context/specs/` の frontmatter、`related_ids` の自己参照（#1460 で別途）

## 母集合（規則 1〜10）

**誤りの側から引いた**（規則 1）—— 「状態語彙に当てはまらない 3 列目」を全索引行から走査する。

```
$ python3 -c "…"   # ^| [IADR-XXXX] で始まる全行の 3 列目を語彙と突き合わせる
523 [IADR-0447] -> '2026-09-12'
524 [IADR-0448] -> '2026-09-12'
525 [IADR-0449] -> '2026-09-12'
526 [IADR-0450] -> '2026-09-13'
```

| 軸 | 結果 | 扱い |
| --- | --- | --- |
| 索引 3 列目の語彙（446 行） | 違反 4 行。他は `Accepted` 408 / `Proposed` 31 / `Superseded by …` 7 | **対象** |
| 索引 3 列目と本体 `status:` の一致（先頭の状態語で比較） | 食い違い 4 件（上と同一集合） | **対象**（別の軸で引き直しても同じ 4 件だと確かめた＝規則 5） |
| 本体 `status:` の値域（452 件） | `Accepted` 414 / `Proposed` 31 / `Superseded` 7。**語彙外なし** | 対象外（既に揃っている） |
| 索引の 2 列目（タイトル） | 既存のラチェットが見ている | 対象外（**同じ不変条件を 2 本持たない**） |
| 計画 ADR（`ADR-XXXX`）の索引 | 本リポジトリに実体が無い（planning 側） | 対象外 |

**自己参照の扱い（規則 8）**: 本仕様書と #1459 の本文は走査語（`Accepted` 等）を含むが、**走査対象は `.ai-context/adr/README.md` の索引行だけ**であり母集合は動かない。

## 設計

### 1. 索引 4 行の是正

3 列目を本体 frontmatter の `status:` に戻す（4 件とも `Accepted`）。**日付は索引へ持たせない** —— 本体の `created:` / `updated:` が正本であり、索引へ複写すると片方が腐る。

### 2. 検査（`scripts/scripts.repo.test.js`）

既存の `inspectAdrIndexTitles` と**同じ場所・同じ作法**（純関数 ＋ 正例 ＋ 変異試験）で `inspectAdrIndexStatuses` を足す。索引行の抽出は `check-adr-numbering.js` の `INDEX_LINE_RE` を**借りる**（[[IADR-0144]] 決定 5。リテラルを複製しない）。

| 違反 | 意味 |
| --- | --- |
| `status-missing` | 3 列目が無い / 空（**2 列で書いた行**もここで落ちる） |
| `status-vocabulary` | 語彙（`Accepted` / `Proposed` / `Deprecated` / `Superseded`）で始まらない |
| `status-mismatch` | **本体 `status:` と食い違う**（先頭の状態語で比較） |

🔴 **baseline を置かない。** 是正後の違反は 0 件であり、ラチェットにすると 4 行が「許容された残件」として固定される。**0 件を維持する検査**にする。

🔴 **比較は先頭の状態語だけで行う。** 索引は `Superseded by [IADR-0282](./…md) / [IADR-0321](…)` のようにリンク付きで後継を並べ、本体は `Superseded` とだけ書く（実測: 本体の値域は 3 種）。**全文一致を課すと既存の 7 行が一斉に赤になる**ため、語だけを突き合わせる。後継 ID の書式は `traceability.repo.md`「Superseded / Deprecated な ADR を引用するときの書式」が持つ。

## 受け入れ基準（#1459 の写像）

- [x] 索引 4 行の 3 列目が本体 `status:` と同じ状態語であり、日付が残っていない
- [x] 索引全行の 3 列目が状態語彙に収まる（違反 0 件）
- [x] 状態列へ日付・自由文を入れる変異で落ちる（正例と対）
- [x] 索引と本体が食い違う変異で落ちる（索引だけを見ると取り逃す型）
- [x] `Superseded by …` の行は通る（陽性対照。既存 7 行を赤にしない）
- [x] 2 列で書いた行が落ちる（#1458 で実際に起きた型）
- [x] `check-adr-numbering` / `check-doc-links` が緑

## テスト方針

`scripts/scripts.repo.test.js` に正例 1・変異 4・実データ 1 を置く。実データ側は**走査 0 件で緑になる fail-open を塞ぐ**（索引行数 ≧ 本体数、本体 `status:` を読めた数 ≧ 本体数）——既存のタイトル側ラチェットと同じ守り方である。

## 検証（`/verify` 相当）

`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` / `node scripts/check-adr-numbering.js` / `node scripts/check-doc-links.js` / `node scripts/check-doc-type-vocabulary.js` / `node scripts/check-doc-status-vocabulary.js` / `node scripts/gen-knowledge-graph.js --check` / `node scripts/check-trace-blocks.js` / `node scripts/check-plan-id-qualification.js` / `node scripts/check-cross-repo-refs.js` / `node scripts/check-commit-messages.js --range origin/develop..HEAD`。

**フロントの検査（vitest / lint / typecheck / knip / chunk-budget）は本作業の差分が触れないため走らせない**（`scripts/` と `.ai-context/` のみ）。
