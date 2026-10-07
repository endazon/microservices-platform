---
title: 作業仕様書 — blocked 判定に再検証の期限を付ける（規約・issue テンプレート・棚卸しの日数基準・8 件の issue）（#1773・第 4 回全体監査 B-16 / C-1）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0506
author: claude
created: 2026-10-07
updated: 2026-10-07
plan_refs:
  - planning:docs/ai-implementation-workflow-guide.md §6（「AI だけでは完結しない」判定には再検証の期限を付ける／棚卸しごとに再検証する／実装リポ側には blocked ラベルと台帳記録を残す）
related_specs: []
issue: "#1773"
---

# 作業仕様書 — blocked 判定に再検証の期限を付ける

## 目的と射程

第 4 回全体監査（2026-10-07）観点 6 の指摘 B-16・C-1 を反映する。基点は `origin/develop` `cffeef4e`（`git rev-parse --is-shallow-repository` → `false`）。

- 受け入れ基準 1: `CLAUDE.md:45` と `AGENTS.md:65` に「再検証の期限を付ける」を入れる（必読予算内）。8 件の open blocked issue の本文に期限を書く。
- 受け入れ基準 2: ラベル種別を実体に合わせる（素の `blocked` を残さない）。
- 受け入れ基準 3: 台帳の要否を決めて記録する（IADR-0506）。
- 受け入れ基準 4（任意）: `scripts/backlog-audit.js` の日数の基準を、ラベル操作で戻らない形にする。**実施する**（変更が小さく、自己試験で固定できるため）。

**射程外**: 8 件の blocked そのものの解消。#1534 の受け入れ基準 1（2 つの `WebApplicationFactory` の統合テスト）は AI の作業として issue に切り出すだけで、本 PR では書かない（宣言ファイル領域 `Platform.Bff.Tests/**` が本 issue の領域外）。

## 母集合の引き方（規則 9・10）

**軸 1（規約の写し）**: `grep -rn "棚卸しごとに再検証" --include=*.js --include=*.md --include=*.yml .`（`src/ai-stock-trading`・`.ai-context/specs`・`CHANGELOG.md` を除く）

| ヒット | 扱い |
| --- | --- |
| `CLAUDE.md:45` / `AGENTS.md:65` | 期限を足す（基準 1） |
| `.github/workflows/backlog-audit.yml:10` / `scripts/README.md:52`（「更新の止まった」） | **基準 4 の変更で新たに誤りになる記述**（規則 10）。期限なし・期限切れへ書き換える |
| `scripts/backlog-audit.js:18-19` | 同上（ヘッダコメント） |

**軸 2（自己試験の件数）**: `scripts/README.md:52` の `--self-test`（7 件）→ 8 件（導出値は実行し直して数えた）。

**軸 3（issue テンプレート）**: `.github/ISSUE_TEMPLATE/` は `ai-implementation.yml` / `implementation-task.md` / `bug_report.md` / `config.yml`。blocked 専用のテンプレートは無い。実装を扱う 2 つに任意欄を足す（`bug_report.md` は不具合の報告で、blocked 判定は起票後に付くため足さない）。

## 決定（詳細は IADR-0506）

1. 台帳は別ファイルに置かない。issue 本文（またはコメント）の `再検証期限: YYYY-MM-DD` を定型欄にし、週次棚卸しの報告が blocked issue の全件を期限つきで並べる（生成される台帳）。
2. 棚卸しは「期限が無い」「期限の日を過ぎた」blocked issue を挙げる。経過日数は `updated_at` ではなく最後のコメントの作成時刻から数える。素の `blocked` は報告に「種別なし」と出す。
3. 8 件の期限は 2026-10-19（月）とする。週次棚卸しの cron（月曜 21:00 UTC）の 2 回目の実行日で、その日の実行で期限切れとして挙がる。

## 受け入れ基準とテストの写像

| 基準 | 確認 |
| --- | --- |
| 1（規約） | `node scripts/check-reading-budget.js` → Claude 集合 43,838 B（85.6%） |
| 1（本文） / 2（ラベル） | GitHub 側の操作。PR 本文と issue のコメントに記録 |
| 3 | IADR-0506 と索引の行 |
| 4 | `node scripts/backlog-audit.js --self-test`（8 件）。期限の読み取り（本文・コメント・テンプレート欄の形・未記入）、ラベル操作で日数が戻らないこと、期限なし・期限切れの抽出、報告の台帳行。`scripts/scripts.repo.test.js` の #1773 節がテンプレートの欄名を棚卸しの関数に読ませて固定する |
