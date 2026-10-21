---
title: 作業仕様書 — AI レビューの Bash の時間上限を広げ、検証の下限の全件の試験を前景で走らせる
type: spec
status: done
related_ids: [NFR, IADR-0538]
author: claude
created: 2026-10-22
updated: 2026-10-22
issue: "#2051"
---

# 作業仕様書 — AI レビューの Bash の時間上限を広げる

> 本仕様書は実装着手前に作成した（2026-10-22）。変更は `.github/workflows/claude-code-review.yml` の 1 ファイルだけ。起動条件と必須チェックは変えない。

## 起点

- PR #2051 の claude-review が、再実行を含めて 2 回続けて判定を投稿せずに終わった（Check review verdict が「見出しの欠落」と「検証の下限の未達」を報告）。
- 原因: 検証の下限（IADR-0538）が求める `scripts.test.js` の全件は約 2 分 40 秒かかる（2026-10-22 の手元の実測。1138 件）。Claude Code の Bash の既定の上限は 120 秒で、超えた呼び出しはバックグラウンドへ移る。agent は結果を待ったままターンを終え、最終のコメントを書かない。
- 対照: 同じ PR で、試験が途中で落ちて 2 分以内に返った head のレビューは、判定を投稿できていた。

## 方式（二重化）

1. 「Run Claude Code Review」の env に `BASH_DEFAULT_TIMEOUT_MS: "600000"` と `BASH_MAX_TIMEOUT_MS: "900000"` を足す（ジョブの timeout-minutes 20 の内側）。
2. プロンプトの `scripts.test.js` の行に「timeout 600000 の前景で走らせ、バックグラウンドにしない」を書き足す。

## 受け入れ基準

| # | 基準 | 確かめ方 |
| --- | --- | --- |
| AC1 | 起動条件（`on:`）と job 名・必須チェックが変わらない | `git diff` が env とプロンプトの行だけ |
| AC2 | 既存の試験・検査器が緑 | `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・`check-workflow-job-refs`・`check-ai-workflow-config` |
| AC3 | この PR 自身のレビューが判定を投稿する（`.github/` の差分なので全件の試験が下限に入る） | PR の claude-review が success |
