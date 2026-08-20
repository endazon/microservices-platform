---
title: 作業仕様書 — claude-code-review の permission_denials を解消し、レビュー投稿を保証する
type: spec
status: completed
related_ids:
  - NFR
author: claude
created: 2026-06-28
updated: 2026-06-28
plan_refs:
  - planning:projects/microservices-platform/06_technical/01_architecture-overview.md
related_specs:
  - ../../docs/operations/operations.md
issue: "#40"
---

# 作業仕様書: claude-code-review の permission_denials 解消とレビュー投稿の保証

## 目的

PR の自動 AI レビュー（`.github/workflows/claude-code-review.yml`）で
`permission_denials_count:1` が発生し、レビュー結果が PR に投稿されない事象を解消する。
あわせて `@claude` 対話ワークフロー（`claude.yml`）の MCP ツール許可を CI で正しく機能させる。

> 本作業は CI/インフラ設定の修正であり、計画書（FR/UC）由来の機能変更を伴わない。
> CLAUDE.md の「仕様書なし着手の禁止」に対し、軽微な運用修正であっても本作業仕様書を
> 後追いで作成してトレーサビリティを残す（前回レビューの指摘 🟢 への対応）。

## 原因

1. **MCP ツール権限が CI に伝わっていなかった**
   `anthropics/claude-code-action` はリポジトリの `.claude/settings.json` を
   自動読込しない。CI でのツール許可は各ワークフローの `claude_args`（`--allowedTools`）で
   指定する必要があるが、GitHub MCP ツールが列挙されていなかった。

2. **MCP ツール名が公式サーバーと不一致**
   `mcp__github__*` は `action.yml` 上で公式 `ghcr.io/github/github-mcp-server`（v0.17.1）を
   起動して提供される。以下のツール名が公式定義と異なっていた。

   | 誤 | 正 |
   |---|---|
   | `mcp__github__list_pull_request_files` | `mcp__github__get_pull_request_files` |
   | `mcp__github__create_pull_request_review` | `mcp__github__create_and_submit_pull_request_review` |
   | `mcp__github__add_pull_request_review_comment` | `mcp__github__add_comment_to_pending_review` |
   | `mcp__github__create_issue_comment` | （公式に存在せず・`add_issue_comment` に統合） |
   | `mcp__github__update_issue_comment` | （公式に存在せず・削除） |

## 対応方針

- **レビュー投稿の保証**: `use_sticky_comment: true` を設定する。これは「ツール拒否時の
  フォールバック」ではなく、**アクション自身が必ず PR にスティッキーコメントを投稿する**
  機能であり、Claude が MCP ツールを呼べるか否かに関わらずレビュー結果が届く。
- **MCP 認証**: `github_token: ${{ secrets.GITHUB_TOKEN }}` を渡す（MCP GitHub ツールと
  スティッキーコメント投稿の認証に必要）。
- **ツール許可**: `claude_args` の `--allowedTools` に公式名の GitHub MCP ツールを列挙する。
- **ローカル整合**: `.claude/settings.json`（ローカル開発用）のツール名も公式名に揃える。

## 受け入れ基準

- [ ] `claude-code-review.yml` の `--allowedTools` が公式 `github-mcp-server` のツール名と一致する。
- [ ] `claude.yml` に `github_token` が渡され、`--allowedTools` が `settings.json` と整合する。
- [ ] `use_sticky_comment: true` により、ツール権限の成否に関わらずレビュー結果が PR に投稿される。
- [ ] `permission_denials_count` が 0 になる。

## 補足: 検証済みの誤検知

前回 AI レビューで「幻覚パラメータの可能性」とされた `display_report` / `track_progress` は、
`action.yml`（L136, L152）に定義された**有効な入力**であり、削除不要。`github_token`（L88）・
`use_sticky_comment`（L112）・`additional_permissions`（L108）も同様に有効。
