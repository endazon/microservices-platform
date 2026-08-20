---
description: 実装中に判明した計画書の問題を計画リポジトリへフィードバックする
argument-hint: <FR-xx | UC-xx | topic>（対象の起点 ID か概要）
allowed-tools: Read, Grep, Glob, Write, Bash(ls:*), Bash(mkdir:*), Bash(git log:*)
---

引数 `$ARGUMENTS`: フィードバック対象の起点 ID（`FR-xx` / `UC-xx` / `SC-xx` / `ADR-xxxx`）または概要。

目的: 実装中に判明した計画書（`project-planning`）の誤り・不足・新たな制約を、計画側へ環流する。

手順:

1. `plan-feedbacker` サブエージェントに起票を委譲する（反映案の起草）。
2. 種別を判定する: `要求の誤り` / `要求の不足` / `UC/画面の差異` / `新たな制約(ADR要)` / `用語追加` / `その他`。
3. 計画リポジトリ project-planning への **GitHub Issue** で起票する（本リポジトリはファイルによる
   環流記録を持たない。ADR-0048 決定 5）。計画リポジトリの `feedback` Issue テンプレートに合わせた
   **Issue 本文（タイトル・本文）を生成**し、GitHub MCP/`gh` で起票する（`decision-needed` ラベルを
   付ける）。起票できない場合は貼り付け用の本文を提示する。
   起票前に、実装側の環流ファイル名・概要で既存 Issue を検索し、重複起票を避ける。
4. 裁定の完了記録は計画側 `projects/<name>/10_feedback/` に残る（本リポジトリには残さない）。
   起票した Issue の URL と、計画側で想定される反映先（要求更新 / 新 ADR / 用語追加 等）を報告する。

注意: 記録は事実と提案を分けて書く。計画書の確定変更は計画側（`/triage-feedback` と人間）が判断する。
