---
title: "実装ADR frontmatter の `related_ids` から自己参照を外し、規約と検査で閉じる（#1460）"
type: spec
status: done
related_ids: [IADR-0385, IADR-0387, IADR-0445, IADR-0446, IADR-0447, IADR-0448, IADR-0449, IADR-0450, IADR-0451, IADR-0452]
author: Claude
created: 2026-09-13
updated: 2026-09-13
plan_refs: []
---

# 仕様書: `related_ids` の自己参照を外す

> 本作業は**メタ作業（文書統制・検査器の追加）**であり、計画書由来の起点 ID を持たない。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）/ ユースケース（UC）/ 画面（SC）: なし
- 非機能要件（NFR）: **無採番**（planning#311 の裁定。工程の管理は製品の非機能要件表に番号を持たない。**環流しない**）
- 起票: #1460。決定の記録: IADR-0452（本作業で起草）

## 目的・背景

実装ADR の frontmatter `related_ids` は「この決定が**関係する他の** ID」を並べる欄だが、**自分自身の ID を書いている記録が 9 件**ある。自己参照は情報を持たず、`gen-knowledge-graph.js` が作る辺としては自己ループになる。

**放置すると逆の形が定着する**: 9 件のうち 7 件が `IADR-0445`〜`0451` の連番であり、**直近 7 件のうち 6 件**が該当する。多数派（自己参照なし 444 件）とどちらが正なのか、書き手が判断できなくなる。

## 対象範囲

- 対象: `.ai-context/adr/IADR-*.md` の frontmatter `related_ids` から自 ID を外す（**9 件**）、規約 1 行、機械検査
- 対象外: 本文（凍結記録の射程）、`.ai-context/specs/` と `.ai-context/superpowers/` の frontmatter、`docs/` の trace ブロック（後述）

## 母集合（規則 1〜10）

**誤りの側から引いた**（規則 1）—— 「`related_ids` に自 ID が現れる」で追跡下の実装ADR 453 件を全数走査した。

🔴 **1 回目の走査は母集合を取りこぼした（規則 2 の破れ。実測）。** `related_ids` の YAML には
**フロー形式 `[A, B]`（131 件）とブロック形式 `- A` の並び（322 件）の 2 つ**があり、
起票時の走査は**フロー形式しか読まない正規表現**だった。**多数派であるブロック形式 322 件を丸ごと
見ていない**まま「8 件」と数えており、検査を書いて全件を読ませた時点で **`IADR-0385`（ブロック形式）**
が 1 件出てきた。**正しい母集合は 9 件**である。#1460 の本文と起票時の数えはこの点で誤っている。

```
IADR-0385, IADR-0387, IADR-0445, IADR-0446, IADR-0447, IADR-0448, IADR-0449, IADR-0450, IADR-0451
9 of 453（related_ids を持つ実装ADR は 453 件＝全件。フロー 131 / ブロック 322）
```

| 軸 | 結果 | 扱い |
| --- | --- | --- |
| 実装ADR の frontmatter `related_ids`（**両形式**） | **9 件**（上記） | **対象** |
| `.ai-context/specs/` の `related_ids` | 仕様書は自分の ID を持たない（ファイル名で識別する）ので**自己参照の概念が無い** | 対象外 |
| `docs/` の trace ブロック（`iadrs:` 等） | 参照する側と参照される側が別文書であり、自己参照が起こり得ない | 対象外 |
| 実装ADR の**本文**の「関連 IADR」行 | 自 ID を書いた例は無い（走査で 0 件） | 対象外 |
| 計画 ADR（planning 側） | 本リポジトリに実体が無い | 対象外 |

**軸を変えて引き直した**（規則 5）: ①frontmatter の文字列一致（**両形式**） ②`gen-knowledge-graph` が作る辺の自己ループ —— **どちらも同じ 9 件**であった。**1 軸目を 1 つの書き方だけで引いた最初の走査が 1 件を落としており、規則 2 と規則 5 は別々に効く**ことが実測できた。

**自己参照の扱い（規則 8）**: 本仕様書と IADR-0452 は 9 件の ID を**列挙する**が、走査対象は `.ai-context/adr/IADR-*.md` の frontmatter だけであり、母集合は動かない。**ただし IADR-0452 自身の `related_ids` に `IADR-0452` を書かない**（書くと 9 件目になる）。

## 設計（決定は IADR-0452）

1. **`related_ids` に自 ID を書かない。** 多数派 444 件の形へ寄せる。理由は IADR-0452 に記す。
2. **他の ID の並びと順序は変えない。** 自 ID の項目だけを落とす（差分を最小にし、レビューで「何が変わったか」を 1 目で読めるようにする）。
3. **本文は書き換えない。** 凍結記録の本文への後付け注記は行わない（`.claude/rules/traceability.repo.md`「凍結の射程」。**frontmatter は対象外**と明記されている）。
4. **機械検査を置く。** `scripts/scripts.repo.test.js` に純関数 ＋ 正例 ＋ 変異試験 ＋ 実データ（違反 0）を足す。#1459 と同じ作法で、**baseline は置かない**。
5. **規約は 1 行。** `.claude/rules/traceability.repo.md` へ足す。必読規約の総量は予算 51,200 バイトに対し **46,076 バイト（90%）**であり、足せるのは 1 行までである（`check-reading-budget.js` で実測）。

## 受け入れ基準（#1460 の写像）

- [x] 9 件の `related_ids` から自 ID が消えている（他の ID の並びと順序は不変）
- [x] 追跡下の実装ADR 全件で自己参照が 0 件
- [x] 自 ID を書く変異で `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が落ちる（正例と対）
- [x] `.claude/rules/traceability.repo.md` から規約が 1 行で読める
- [x] `gen-knowledge-graph --check` が緑で、**自己ループが 1 → 0 になる**（当初「辺が 9 本減る」と書いたが**実測は 1 本**である。理由は下記 §発見）
- [x] `check-reading-budget` が予算内
- [x] `check-adr-numbering` / `check-doc-links` が緑

## 発見（#1462 として切り出した）

受け入れ基準の「辺が 9 本減る」を実測したところ、**減ったのは 1 本だけ**だった。原因は
**`gen-knowledge-graph.js` の `yamlListField()` もブロック形式しか読まない**ことである
（`related_ids: [A, B]` のフロー形式は 1 件もエッジにならない）。自己参照 9 件のうち
グラフに載っていたのは `IADR-0385`（ブロック形式）だけだった。

実測: ブロック形式の欄 1,399 に対し**フロー形式 323**、**載っていない参照は 3,567 件**（322 ファイル）。
現在のエッジ総数 11,586 に対し約 23% 相当が欠落しており、`--check`（エッジ先の実在）も
その範囲を**検査していない**。

**同じ「YAML の 2 形式」の穴が、人の走査・本作業の検査・グラフ生成器の 3 箇所に空いていた。**
本作業では検査側だけを両形式対応にし、**生成器の是正は #1462 へ切り出す**（参照切れが大量に
出る可能性があり、その是正まで抱えると本作業の範囲が読めなくなる）。

## テスト方針

`scripts/scripts.repo.test.js` に正例 1・変異 3（フロー / 本文の自 ID / **ブロック形式**）・境界 1・実データ 1。**パーサが両形式を読むことをテストで固定する** —— 片方しか読まない実装は、実データ側でも静かに緑になるためである。実データ側は**走査 0 件で緑になる fail-open を塞ぐ**（`related_ids` を読めた件数の下限を置く）——隣の索引検査と同じ守り方である。

## 検証（`/verify` 相当）

`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` / `node scripts/gen-knowledge-graph.js --check`（辺の増減を before/after で数える）/ `node scripts/check-adr-numbering.js` / `node scripts/check-reading-budget.js` / `node scripts/check-doc-links.js` / `node scripts/check-doc-type-vocabulary.js` / `node scripts/check-doc-status-vocabulary.js` / `node scripts/check-trace-blocks.js` / `node scripts/check-plan-id-qualification.js` / `node scripts/check-cross-repo-refs.js` / `node scripts/check-commit-messages.js --range origin/develop..HEAD`。

**フロントの検査は走らせない**（差分が `src/` に触れない）。
