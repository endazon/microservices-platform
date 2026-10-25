---
title: 作業仕様書 — AI レビューの下限「scripts.test.js 全件」を、作業ツリーの復元の有無を問わず同じ head の CI scripts-tests の success で満たす
type: spec
status: done
related_ids: [NFR, IADR-0617, IADR-0615, IADR-0538]
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:docs/ai-implementation-workflow-guide.md（監査は証跡必須）
issue: "#2206"
---

# 作業仕様書 — 下限「scripts.test.js 全件」の代わりの根拠から復元の条件を外す（#2206）

> 本仕様書は実装着手前に作成した（着手 2026-10-24）。判断の記録は **IADR-0617**（新規。IADR-0615 を改める）に置く。

## 事象

- MSP#2205 で `claude-review` が 2 回続けて赤になった。理由はどちらも下限「`scripts/` か `.github/` の差分なら `scripts.test.js` 全件」の未達である。コードの指摘は 🔴0・🟡0 だった。
  - `scripts/` の差分は `scripts/chunk-budget-baseline.json` の数値だけで、レビュアーはこれを見落とし「scripts/ .github/ の差分は無い」と書いた。
  - 同じ head の CI `scripts-tests` は success だった。
- IADR-0615 の代わりの根拠（同じ head の CI `scripts-tests` の success）は、作業ツリーが PR 版と違う（`RESTORED_FILES` が空でない）ときに限って効く。MSP#2205 は復元なしなので効かなかった。
- さらに、判定のステップ（`claude-code-review.yml`）は `RESTORED_FILES` が空なら check-run を問い合わせず、`SCRIPTS_TESTS_CONCLUSION` を空のまま渡していた。検査器だけ直しても効かない。

## 受け入れ基準

- [x] MSP#2205 と同じ形（`scripts/` の数値だけの差分・復元なし・CI の `scripts-tests` が success）で、`check-review-verdict` が下限を満たしたと判定する。
- [x] `RESTORED_FILES` が空で `SCRIPTS_TESTS_CONCLUSION` が failure・cancelled・timed_out・空文字なら、従来どおり下限未達になる。
- [x] `RESTORED_FILES` ありの既存の試験（MSP#2183 の実本文を含む）が通る。
- [x] 判定のステップが、復元の有無にかかわらず、`scripts/` か `.github/` の差分があれば check-run を待って `SCRIPTS_TESTS_CONCLUSION` を渡す。
- [x] 他の下限（変更ファイルを全部読む・helm・自己申告の違反）は変えない。起動条件（`pull_request`）と必須チェックの名前（`claude-review`）を変えない。

## 方式（IADR-0617）

- `scripts/check-review-verdict.js`:
  - `ciScriptsTestsStandsIn(evidence)` を `evidence.scriptsTestsConclusion === 'success'` だけにする。
  - 未達の文言は、`scripts-tests` の結果を常に添える（復元の有無を問わない）。
  - 代わりの根拠を使ったときのログに、復元の有無を併記する。
  - 自己試験: 「作業ツリーが PR 版と同じなら CI が success でも赤」の陽性対照を、「復元なしでも success なら緑・success 以外は赤」へ改める。
- `.github/workflows/claude-code-review.yml`:
  - 判定のステップの待ちの条件から `[ -n "$RESTORED_FILES" ]` を外す。`RESTORED_FILES` は検査器のログのために引き続き渡す。
  - 条件の判定に使う `printf … | grep -qE` は、#2202 の SIGPIPE の形を避けて `case`（パイプなし）にする。
  - プロンプトの下限の節に、CI の success でも満たす旨を書く。レビュアーには引き続き全件の実行を求める（求める検証は減らさない）。
- `scripts/scripts.repo.test.js`:
  - MSP#2183 の実本文の陽性対照から `{ restoredFiles: [], scriptsTestsConclusion: 'success' }` を外し、緑の場合へ移す。
  - MSP#2205 の形（`scripts/chunk-budget-baseline.json` だけ・復元なし）の緑と陽性対照（failure・cancelled・timed_out・空文字・無し）を足す。
  - 配線の固定に「待ちの条件が `RESTORED_FILES` に依らない」ことを足し、陰性対照（条件を戻すと赤）を置く。

## 母集合（規則 9・10）

- 誤りの側の文字列: `RESTORED_FILES`・`IADR-0615`・「作業ツリーが PR 版と違うときに限り」「両方を要る」「作業ツリーが PR 版と同じなら」。リポ全体を `grep -rn` で走査した。
- 直したもの: `scripts/check-review-verdict.js`（ヘッダ・`ciScriptsTestsStandsIn`・文言・自己試験）、`.github/workflows/claude-code-review.yml`（権限のコメント・プロンプト・判定のステップとそのコメント）、`scripts/scripts.repo.test.js`（実本文の試験・配線の固定・権限表のコメント）、IADR-0615（後継の併記）、索引。
- 除外したもの: 作業仕様書 `20261024_2184_review-floor-ci-scripts-tests.md`（point-in-time の記録）、IADR-0615 の本文の決定（凍結。追記ブロックで後継を示す）。

## 検証

```bash
node scripts/check-review-verdict.js --self-test
REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js
bash msp-doc-checks.sh <worktree>
# 判定のステップの run を取り出して bash -n
```
