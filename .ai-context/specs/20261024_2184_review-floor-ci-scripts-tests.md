---
title: 作業仕様書 — AI レビューの `.claude/` 復元と下限「scripts.test.js 全件」の衝突を、同じ head の CI scripts-tests で解く
type: spec
status: done
related_ids: [NFR, NFR-30, IADR-0615, IADR-0538, IADR-0198, IADR-0222, IADR-0614]
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（NFR-30。範囲の追随の出典）
issue: "#2184"
---

# 作業仕様書 — レビューの下限と `.claude/` 復元の衝突（#2184）

> 本仕様書は実装着手前に作成した（着手 2026-10-24）。判断の記録は **IADR-0615**（新規）に置く。

## 事象

- MSP#2183 で、claude-review が下限「`scripts/` `.github/` の差分なら `scripts.test.js` 全件」によって 3 回赤になった。
  - レビュアーは前景で実行していた。しかし、統合ブランチ版へ戻された `.claude/rules/traceability.repo.md`（`NFR-29`）と、
    PR 版の固定値（`NFR-30`）が食い違い、`scripts.repo.test.js:9217` で止まった。
  - 同じ head の CI `scripts-tests` は success だった。

## 受け入れ基準

- [x] `.claude/rules/*.md` と `scripts/scripts.repo.test.js` を同時に変える PR で、claude-review が下限を満たして緑になる。
  本 PR 自身が実例である（NFR の範囲の追随一式を同梱）。実地の確認は PR の CI で行う。
- [x] `scripts-tests` が赤（failure・cancelled・timed_out・結果なし）の head では、従来どおり下限未達になる（陽性対照の試験）。
- [x] 起動条件（`pull_request`）と必須チェックの名前（`claude-review`）を変えない。

## 先に確かめたこと

- **ワークフローの変更は、PR 自身のレビューに効く。** `claude-code-review.yml` の起動条件は `pull_request` である（`pull_request_target` ではない）。
  - 同一リポジトリのブランチからの PR では、PR 版のワークフローと PR 版の `scripts/check-review-verdict.js` が走る。
  - アクションが統合ブランチ版へ戻すのは `.claude/` 等だけで、`scripts/` は戻さない。
  - したがって、NFR の範囲の追随一式を同じ PR に入れる。
- `scripts-tests` と `claude-review` は同時に始まる。所要時間は実測で、`scripts-tests` が約 1 分 30 秒、レビューが約 2 分である（MSP#2183 の head `4acf862d`）。
  待ちの上限を 10 分とし、ジョブの上限 20 分の内側に収める。

## 方式（IADR-0615）

- `scripts/check-review-verdict.js` の変更:
  - `findFloorViolations(body, changedFiles, evidence)` に `evidence = { restoredFiles, scriptsTestsConclusion }` を足した。
  - 作業ツリーが PR 版と違い、かつ CI が `success` のときだけ、`scripts.test.js` の記載の欠落を許す。
  - 環境変数 `RESTORED_FILES` / `SCRIPTS_TESTS_CONCLUSION` を読む。
- `.github/workflows/claude-code-review.yml` の変更:
  - 判定のステップで `git diff --name-only HEAD` を取り、作業ツリーと PR 版の差を測る。
  - 差があり、かつ `scripts/` か `.github/` の差分があるときだけ、同じ head の check-run `scripts-tests` を待つ。対象は `app.slug == github-actions` のもので、最新を取る。
  - ジョブの権限に `checks: read` を足した。
  - プロンプトに、復元のせいで落ちた試験の書き方を 1 項目足した。
- 試験:
  - 検査器の自己試験を 3 件足した。緑の場合が 1 件、陽性対照が 2 件である。
  - `scripts.repo.test.js` に次を足した。
    - 配線の固定。`RESTORED_FILES`・`SCRIPTS_TESTS_CONCLUSION`・`check_name=scripts-tests`・`checks: read` と、それぞれの陰性対照。
    - MSP#2183 の実レビュー本文（`scripts/fixtures/review-floor/pr2183-review3.txt`）での緑の場合と陽性対照。
    - 権限の表 `JOB_SCOPES` に `checks: read` を足した。表は作業仕様書の表と同時に直す規律なので、ここに書く。
      変更後の表の行: `claude-code-review.yml:claude-review` = `actions: read`・`checks: read`・`id-token: write`・`issues: write`・`pull-requests: write`。
- 同梱する NFR の範囲の追随（MSP#2183 から外した一式。コミット 7cb15b3e / 72fba6b1 の差分を再利用）:
  - `.claude/rules/traceability.repo.md`: `NFR-01`〜`NFR-30`。
  - 別紙: ［2026-10-24］の節・§4・trace ブロック。
  - `scripts/scripts.repo.test.js`: 固定値 2 か所・注記 2 か所。
  - `scripts/check-planning-adr-range.js`: 注記・エラー文・書式の例。

## 母集合（規則 9・10）

- 誤りの側の文字列は次のとおりである。リポ全体を走査した（MSP#2183 の作業仕様書の判断 6 の追記と同じ母集合・同じ除外）。
  - 下限の記述: `scripts.test.js` の下限・`CHANGED_FILES`・`check-review-verdict`
  - NFR の範囲: `NFR-29`・`NFR-01..29`・`NFR-01`〜`NFR-29`
- 直したもの:
  - 下限: プロンプトの下限の節、判定のステップ、`scripts.repo.test.js` の対の固定。
  - NFR の範囲: 上の一式。
- 除外したもの:
  - IADR-0538 の本文（凍結記録）。
  - 別紙の過去の節（当時の記述）。
  - 自己試験の固定値（`check-planning-adr-range.js:611・613`・`backlog-audit.js:371`）。宣言の遅れを意図して作った値である。
  - `NFR-29` を ID として引く箇所。
- 別件として報告: 計画 `origin/main`（`a47c050`）の計画 ADR は `0001..0143` で、宣言 `0001..0141` は 2 件遅れている。本 PR では引き直さない。

## 検証

```bash
node scripts/check-review-verdict.js --self-test          # 25 件
REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js          # 全件
# 判定のステップの run を取り出して bash -n と擬似実行。HEAD_SHA=72fba6b1（scripts-tests が success）に対し、作業ツリーへ差を作って success を観測した
```
