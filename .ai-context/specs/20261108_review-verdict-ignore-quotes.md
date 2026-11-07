---
title: 作業仕様書 — check-review-verdict が、レビュー本文の引用と、変更ファイル以外の物（コマンドの出力）を読んでいないと書いた文を、下限の自己申告と読み違えないようにする
type: spec
status: in-progress
related_ids: [NFR, IADR-0707, IADR-0538, IADR-0643]
author: claude
created: 2026-11-08
updated: 2026-11-08
related_specs:
  - 20261107_review-verdict-unread-run-result.md
---

# 作業仕様書 — 下限の自己申告の読み違い（引用・出力）

> 着手 2026-11-08（develop `f29b40261`）。判断の記録は IADR-0707。AST にも同じ直しを入れる（AST の IADR-0643）。

## 起点（同型 2 回）

1. #2512（run 42396353370 / job 128474457406）: 本文の
   「試験が固定する 5 語句（流用しない・push のたびに・全ファイルを読み直す・前回以後だけは満たさない・M の数え方）は prompt に存在する。」が、
   「下限の自己申告: 本文が下限を満たしていないと書いている」で落ちた。語句を括弧の中で紹介しただけで、同じ行の「【検証の下限】」と合わさって当たった。
2. AST#1597（run 42382006516 / job 128431415722）: `helm template` の行の「他の Worker の出力は全行を読んでいない。」が、
   「43 ファイルを読んだと申告したが、本文が精読していないと書いている」で落ちた。指しているのは描画の出力で、変更ファイルではない。
   本リポの #2276 の除外（`aboutNonFileObject`）は**行**に拡張子（`values-cloud.yaml` 等。コマンドの引数）があると外れるので、本リポの検査器でも同じく落ちる（実測）。
- どちらも再実行で通った（言い回しが変わったため）。

## 再現（実測）

- 上の 2 件の issue comment（MSP 6710653084・AST 6708323055）の本文と、PR の変更ファイル（`pulls/<n>/files`）を develop の検査器の
  `findFloorViolations` に渡すと、job のログと同じ 1 行ずつが出る。

## 方式

- 引用の外し方（`stripQuotes`）を、自己申告の判定（`findFloorAdmissions`）と未精読の判定（`findNotReadAdmissions`）で共有する。
  - 既存: インラインコード・「」・『』。
  - 足す: 引用ブロック（行頭 `>`）の行は数えない。語句を紹介する括弧（直前が「語句・文言・文字列・フレーズ・表現」の括弧）の中は数えない。
    括弧一般は外さない（「下限は満たしていない（時間切れ）」のような本当の申告の補足を消さないため）。
- 未精読の判定に除外を 1 つ足す（`aboutOutputAsObject`）。次を**すべて**満たす文だけを外す。
  1. 当たった型のすべてで、その直前が「出力・描画・描画結果・ログ・実行結果」＋「は・を・も・の」で終わる（読んでいない物＝出力そのもの）。
  2. 文（引用の中も含む）が、ファイルを指す語（`FILE_TARGET`。差分・ファイル・本文・テスト・拡張子 等）を含まない。
  3. 文（引用の中も含む）が、変更ファイルのパスも名前も含まない。
  判定の単位は**文**（行ではない）。行は条件 1 を満たさない文（「出力が大きく、全行は読んでいない」）では従来どおり #2276 の 3 条件で見る。

## 母集合（規則 9・10）

- 「下限の自己申告」の判定の呼び手: `findFloorAdmissions` は `findFloorViolations` と `scripts.repo.test.js`・`scripts.test.js` だけ（`grep -rn findFloorAdmissions scripts`）。
- `stripQuotes` の呼び手: `findNotReadAdmissions` だけ。広げた分は両方に効く（意図どおり）。
- 試験: `check-review-verdict.js --self-test`・`scripts.test.js`（`findFloorAdmissions` の表）・`scripts.repo.test.js`（実本文の fixture）。

## 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| AC1 | #2512 の実本文（fixture `pr2512-review2.txt`）が赤にならない | `scripts.repo.test.js` |
| AC2 | AST#1597 の実本文（fixture `ast1597-review3.txt`。変更ファイルは当時の 43 件）が赤にならない | 同 |
| AC3 | 陽性: 同じ文を、本当の申告（括弧の外・語句以外の括弧・引用ブロック外）や変更ファイル・差分・テストの出力を指す形、出力が理由の形に書き換えると赤 | 同・`--self-test` |
| AC4 | 陰性対照: 直しを外した（`stripQuotes` の追加と `aboutOutputAsObject` を戻した）検査器では AC1・AC2 が赤 | 手で確かめ、PR 本文に記録 |
| AC5 | 既存の陽性・陰性の試験が全部そのまま緑（消さない・変えない） | `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` |
