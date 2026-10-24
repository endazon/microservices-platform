---
title: IADR-0617 AI レビューの下限「scripts.test.js 全件」は、作業ツリーの復元の有無を問わず同じ head の CI scripts-tests の success でも満たす
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0615, IADR-0538]
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:docs/ai-implementation-workflow-guide.md（監査は証跡必須）
related_specs:
  - ../specs/20261024_2206_review-floor-ci-scripts-tests-without-restore.md
---

# IADR-0617: 下限「scripts.test.js 全件」の代わりの根拠から、作業ツリーの復元の条件を外す

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-24
- 決定者: claude（#2206）

## 起点・関連

- 起点: #2206（MSP#2205 で claude-review が同じ下限によって 2 回赤。コードの指摘は 🔴0・🟡0）。
- 改める実装 ADR: [IADR-0615](IADR-0615_review-floor-ci-scripts-tests-as-evidence-when-config-restored.md)（決定 1・決定 2 の最後の項目・決定 3 の待つ条件）。
- 前提とする実装 ADR: [IADR-0538](IADR-0538_review-floor-verdict-check.md)（検証の下限と `check-review-verdict.js`）。
- 作業仕様書: [20261024_2206_review-floor-ci-scripts-tests-without-restore](../specs/20261024_2206_review-floor-ci-scripts-tests-without-restore.md)。

## コンテキストと課題

- IADR-0615 は、下限「`scripts/` `.github/` の差分なら `scripts.test.js` 全件」の代わりの根拠として、同じ head の CI `scripts-tests` の success を認めた。
  ただし、作業ツリーが PR 版と違う（`RESTORED_FILES` が空でない）ときに限った。
- MSP#2205 の `scripts/` の差分は `scripts/chunk-budget-baseline.json` の数値だけだった。レビュアーはこれを見落とし、
  「`scripts/` `.github/` の差分は無いので `scripts.test.js` は走らせていない」と書いた。復元は無く、同じ head の `scripts-tests` は success だった。
  - IADR-0615 の条件に当たらないので下限未達になり、許された 1 回の再実行も同じ理由で赤になった。
  - 判定のステップも `RESTORED_FILES` が空なら check-run を問い合わせず、`SCRIPTS_TESTS_CONCLUSION` を空で渡していた。
- AI レビューの下限が実情とずれて PR を止めたのは、#2184 に続いて 2 回目である。

## 決定

1. **`check-review-verdict.js` は、同じ head の CI `scripts-tests` の conclusion が `success` であることだけで、下限「`scripts.test.js` 全件」を満たしたとみなす。**
   作業ツリーの復元（`RESTORED_FILES`）の有無は問わない。IADR-0615 決定 1 の「両方を要る」を改める。
2. **success 以外は従来どおり下限未達である。** failure・cancelled・timed_out・最大 10 分待っても完了しない・check-run が無い、のいずれも赤になる（陽性対照の試験で固定する）。
   本文の「確認できたこと」に実行の記載があれば、従来どおりそれでも満たす。他の下限（変更ファイルを全部読む・helm・自己申告の違反）は変えない。
3. **判定のステップは、`scripts/` か `.github/` の差分があれば、復元の有無にかかわらず check-run を待つ。**
   - 待ち方（15 秒ごと・最大 40 回・`app.slug == github-actions` の最新）と権限（`checks: read`）は IADR-0615 のまま。
   - 差分の判定は `case` で行う。`printf … | grep -q` は pipefail の下で書き手が SIGPIPE で落ちうる（#2202）。
   - `RESTORED_FILES` は検査器のログのためだけに渡す。
   - **起動条件（`pull_request`）と必須チェックの名前（`claude-review`）は変えない。**
4. **プロンプトは、レビュアーに引き続き全件の実行を求める。** 求める検証は減らさない。差分の所在は `git diff --name-only` で確かめるよう書き足す。

## 理由

- **`scripts.test.js` 全件は、CI の `scripts-tests` が実行するものと同じ試験である。** 同じ head で CI が success なら、レビュアーがもう一度実行しても独立した保証は増えない。
  IADR-0615 が退けた「作業ツリーの差を問わず CI の success を常に代わりにする」案の理由（下限を不要に緩める）は、この点で根拠を失う。下限が守る対象（PR 版の試験が通ること）は CI が同じ精度で示す。
- **レビュアーが差分の所在を見誤ると、再実行の枠を食い潰す。** 数値だけの baseline の変更のように、見落としやすい `scripts/` の差分は今後も起きる。そのたびにコードの指摘ゼロの PR が止まる。

## 退けた案

- **レビュアーへの指示を強めるだけにする**（「`scripts/` の差分を必ず `git diff` で確かめる」）: 見落としを減らすが、なくせない。止まったときの回復手段が「再実行」しか無いまま残る。指示は決定 4 として併せて書くが、それだけには頼らない。
- **検査器が CHANGED_FILES を本文へ示し直す**: 下限未達の理由は伝わるが、再実行の枠を使う点は同じである。

## 結果

- MSP#2205 と同じ形（`scripts/` の数値だけの差分・復元なし・`scripts-tests` が success）で、`claude-review` は下限を満たす。
- `scripts/` か `.github/` を触る PR では、判定のステップが `scripts-tests` の完了を最大 10 分待つようになる。`scripts-tests` は約 1 分 30 秒、レビューは約 2 分なので、通常は待たない（IADR-0615 の実測）。
- 信頼の境界は IADR-0615 と同じである。PR の作者は CI も検査器も同じ PR で書き換えられる。人のレビューとフェーズ末監査が最後の門である。
- 残余: `scripts-tests` の名前やジョブを変える PR では、代わりの根拠が見つからず赤になる（安全側）。名前を変えるときは、本 IADR の試験と判定のステップを同時に直す。

## 関連

- Amends: [IADR-0615](IADR-0615_review-floor-ci-scripts-tests-as-evidence-when-config-restored.md)（決定 1 の復元の条件・決定 2 の「作業ツリーが PR 版と同じ場合」・決定 3 の待つ条件）
