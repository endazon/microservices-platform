---
title: IADR-0615 AI レビューの作業ツリーが PR 版と違うときに限り、下限「scripts.test.js 全件」を同じ head の CI scripts-tests の success で満たす
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-30, IADR-0538, IADR-0198, IADR-0222, IADR-0614]
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:docs/ai-implementation-workflow-guide.md（監査は証跡必須）
related_specs:
  - ../specs/20261024_2184_review-floor-ci-scripts-tests.md
---

# IADR-0615: 下限「scripts.test.js 全件」の代わりの根拠を、同じ head の CI scripts-tests にする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-24
- 決定者: claude（#2184）

## 起点・関連

- 起点: #2184（MSP#2183 で claude-review が同じ下限によって 3 回赤）。
- 前提とする実装 ADR: [IADR-0538](IADR-0538_review-floor-verdict-check.md)（検証の下限と `check-review-verdict.js`）、
  [IADR-0198](IADR-0198_kit-delta-fifth-kind-and-review-verdict.md)（判定の投稿の検査）、[IADR-0222](IADR-0222_mcp-json-scope-and-github-server-collision.md)（アクションの設定の復元）。
- 作業仕様書: [20261024_2184_review-floor-ci-scripts-tests](../specs/20261024_2184_review-floor-ci-scripts-tests.md)。

## コンテキストと課題

- claude-code-action は、PR head を信用しない扱いのため、`.claude/`・`CLAUDE.md`・`.mcp.json` 等を統合ブランチ版へ戻してからレビュアーを動かす。
- `scripts.repo.test.js` には `.claude/rules/*.md` の文言を固定する試験がある。入口の規範と固定値を同時に直す PR では、レビュアーの作業ツリーで
  この試験が必ず落ちる（PR 版の固定値 × 統合ブランチ版の入口）。試験は最初の失敗で止まるので、全件の合格を示せない。
- 下限（IADR-0538）は「`scripts/` `.github/` の差分なら `scripts.test.js` の実行を『確認できたこと』に書く」であり、上の PR では
  何回レビューし直しても満たせない。一方、PR 版をそのまま検査する CI の `scripts-tests`（`ci.yml`）は同じ head で success だった。

## 決定

1. **案 1 を採る。** `check-review-verdict.js` は、次の 2 つが**両方**成り立つときに限り、下限「`scripts.test.js` 全件」を満たしたとみなす。
   - レビューの作業ツリーが PR 版と違う（`RESTORED_FILES` が空でない）。判定のステップが `git diff --name-only HEAD` で実測する。アクションの内部の一覧は写さない。
   - 同じ head SHA の CI `scripts-tests`（GitHub Actions が出す check-run）の conclusion が `success` である（`SCRIPTS_TESTS_CONCLUSION`）。
2. **代わりの根拠が無ければ、従来どおり未達である。** 次の場合は下限未達のまま赤になる（陽性対照の試験で固定する）。
   - `scripts-tests` が failure・cancelled・timed_out の場合。
   - 最大 10 分待っても完了しない場合。
   - check-run が無い場合。
   - 作業ツリーが PR 版と同じ場合。この場合はレビュアーが PR 版をそのまま回せるので、CI の結果を代わりにしない。
   - 本文に実行の記載があれば、従来どおりそれで満たす。
3. 判定のステップは、条件が揃うとき（作業ツリーの差あり、かつ `scripts/` か `.github/` の差分あり）だけ check-run を待つ。
   - 15 秒ごとに、最大 40 回問い合わせる。
   - 権限として `checks: read` をジョブに足す。権限の表（`scripts.repo.test.js` の `JOB_SCOPES`）も同時に直す。
   - **起動条件（`pull_request`）と必須チェックの名前（`claude-review`）は変えない。**
4. プロンプトには次の 2 点を書く。
   - 復元のせいで落ちた試験は「🔍 実行できなかったこと」に書く。成功と書き換えない。
   - 下限は検査器が CI の結果で確かめる。

## 退けた案

- **案 2（復元の前に AI を介さず `scripts.test.js` を走らせ、結果をレビューへ渡す）**: CI の `scripts-tests` と同じ試験を、もう 1 回走らせることになる。
  - 約 2 分 40 秒かかり、ジョブの時間（上限 20 分）を食う。
  - 結果をレビュアーへ渡す経路（ファイル・プロンプト）が新たに要る。
  - 復元はアクションの内部で行われるので、「前」の時点を外から作るにはアクションの手順に依存する。
- **案 3（作業ツリーと PR 版の差をレビュアーに示し、「CI に委ねる」と書けば満たす扱い）**: 根拠がレビュアーの記述になり、CI が赤でも「委ねる」と書けば通る。
  「実行しなくても通る」形であり、採らない。
- **作業ツリーの差を問わず、CI の success を常に代わりにする**: レビュアーが PR 版を回せる PR でも実行を省けるようになり、下限を不要に緩める。

## 結果

- `.claude/**` と、それを固定する `scripts/` の試験を同時に変える PR でも、下限を満たせる。本 PR 自身がその実例である（NFR の範囲の追随一式を同梱する）。
- 信頼の境界は変わらない。PR の作者は CI の `scripts-tests` を書き換えられるが、検査器（`check-review-verdict.js`）も同じ PR で書き換えられる（`pull_request` は PR 版を走らせる）。
  したがって、新しい穴は開かない。人のレビューとフェーズ末監査が最後の門である。
- 残余: `scripts-tests` の名前やジョブを変える PR では、代わりの根拠が見つからず赤になる（安全側）。名前を変えるときは、本 IADR の試験と判定のステップを同時に直す。
