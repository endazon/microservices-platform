---
title: 他リポジトリ issue 表記（短縮形への統一・列挙形の修飾漏れ）を止める機械がキットに無い — `check-cross-repo-refs.js` の環流提案
type: plan-feedback
status: open
category: ツールチェーン（impl-handoff-kit）
related_ids: [NFR, IADR-0115, IADR-0140]
source_repo: microservices-platform
source_ref: "chore/NFR-507-cross-repo-issue-refs / docs/specs/20260807_issue-507_cross-repo-issue-refs.md（#507）"
author: Claude（実装）
created: 2026-08-07
updated: 2026-08-07
---

# フィードバック: 他リポジトリ issue 表記の規約に対応する機械検査がキットに無い

## 起票状況

| 手順 | 状態 |
| --- | --- |
| 2. `feedback/` への記録作成 | **完了**（本ファイル） |
| 3. 計画リポ（`impl-handoff-kit`）への伝達 | **未実施**。本 PR は push しない運用のため、伝達は監査後に人間が行う |

## 事実

キットが配布する `.claude/rules/traceability.md` は次の 2 つを定めている。

1. 他リポジトリの issue / PR 番号は**短縮形**（`planning#NNN` / `AST#NNN`）へ寄せ、フルパス形式と
   混在させない。
2. **列挙形でも各番号を修飾する**（`planning#NNN / #MMM` の 2 番目以降は本リポジトリの issue へ
   誤リンクする）。

**この 2 つを検査する機械がキットに無い。** キットが配布する検査器のうち

- `scripts/check-commit-messages.js` は件名の**書式**（`種別(起点ID): 要約`）しか見ない
- `scripts/check-doc-links.js` は**相対リンク**の実在しか見ない

結果として、本リポジトリでは規約に反する表記が **88 occurrence** 蓄積していた。さらに
**PR #561 は、その規約が書いてある当のファイルを編集する PR でありながら同じ違反を犯し、
CI を green で通過した**（件名・本文・PR タイトルの 3 面すべて）。規約に書くだけでは再発する。

## 提案

`scripts/check-cross-repo-refs.js`（本 PR で新設）をキットへ取り込むこと。実装は
[IADR-0140](../docs/adr/IADR-0140_cross-repo-issue-ref-checker.md) の 4 決定に従う。とくに次の 2 点は
汎用的で、どの実装リポジトリでも同じ設計になるはずである。

1. **検査対象は表示テキストのみ**（インラインコード／コードフェンスの中は見ない）。自動リンクは
   そこで効かないので実害が無く、**規約自身の反例（`` 誤: `planning#146 / #149 / #160` ``）を
   書けなくなる問題**が除外リスト無しで解決する。
2. **裸の `#NNN` 一般を違反にしない。** 「他リポジトリの修飾語の**直後**に続く列挙」だけを裸と
   判定する。自リポジトリの正当な参照で偽陽性を出せば検査は外される。

## キット側の可変部分

短縮リポ名（本リポでは `planning` / `AST`）とその長い表記（`project-planning` / `ai-stock-trading`）は
プロジェクト固有である。キットへ入れるなら `check-commit-messages.js` の `PLAN_PROJECT` と同じく
**置換点**（定数 + 環境変数での上書き）にするのが素直である。

## 本リポジトリでの固有デルタ

`scripts/check-commit-messages.js` は IADR-0115 の**分類 B**（キット＋固有デルタ）である。本 PR が
足したのは `require` 1 行と呼び出し 2 箇所（許容される固有デルタ種 3 = 本リポにしか存在しない
スクリプトの呼び出し）で、規約の単一情報源である `validateSubject` は変更していない。
キットが本検査器を取り込めば、このデルタは解消できる。
