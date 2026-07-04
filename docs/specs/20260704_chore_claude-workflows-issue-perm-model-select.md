---
title: 作業仕様書 — @claude ワークフローに Issue 発行権限とモデル選択を追加
type: work-spec
status: completed
related_ids:
  - NFR
author: claude
created: 2026-07-04
updated: 2026-07-04
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/01_architecture-overview.md"
related_specs:
  - 20260628_fix_claude-review-permission.md
issue: "#59"
---

# 作業仕様書: @claude ワークフローに Issue 発行権限とモデル選択を追加

## 目的

GitHub 上の `@claude` 対話ワークフローに対する 2 つの要望に対応する。

1. **Issue 発行（作成）権限**: コーディング用・レビュー用ワークフローが、フォロー
   アップの GitHub Issue を新規作成できるようにする。
2. **モデル選択**: `@claude` メンション内の指定でモデルを切り替えられるようにする。

## 現状分析（Issue 作成可否）

Issue の新規作成には「トークン権限 `issues: write`」と「作成手段（ツール）」の
両方が必要。調査結果は以下。

| ファイル | `issues: write` | 作成ツール |
| --- | --- | --- |
| `claude-coding.yml` | あり | なし（`issue_read` / `add_issue_comment` のみ） |
| `claude-code-review.yml` | **なし** | なし |

claude-code-action の同梱 github MCP サーバは read/comment 系のみを提供し、Issue
新規作成の MCP ツールは提供しない（作成系は `gh` CLI にフォールバックする。
参考: anthropics/claude-code-action#723）。したがって作成手段として `gh issue create`
を許可する方針とする。

## 方針

両ワークフローに以下を追加する。

### Issue 発行
- `claude-code-review.yml` の `permissions` に `issues: write` を追加。
- 両ファイルの `--allowedTools` に `Bash(gh issue create:*)` を追加。
- `Run Claude Code` ステップに `env: GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}` を付与し、
  Bash 内 `gh` が既定トークンで認証されるようにする（gh は `GH_TOKEN`/`GITHUB_TOKEN`
  を参照）。

### モデル選択（`@claude <model>`）
- メンション/本文を解析する `Select model` ステップを追加し、`@claude opus` /
  `@claude sonnet` / `@claude haiku`（review は PR 本文・タイトル）でモデルを切替。
  未指定時は既定（coding=`claude-opus-4-8` / review=`claude-sonnet-5`）。
- 本文はコマンドインジェクション防止のため `${{ }}` を run スクリプトへ直接展開せず、
  `env:` 経由で受け取る。
- `claude_args` の `--model <固定>` を `--model ${{ steps.model.outputs.model }}` に置換。

対応モデル ID: `claude-opus-4-8` / `claude-sonnet-5` / `claude-haiku-4-5-20251001`。

## 作業範囲

### 含むもの
- `.github/workflows/claude-coding.yml`: モデル選択ステップ／`GH_TOKEN`／
  `gh issue create` 許可／`--model` 動的化。
- `.github/workflows/claude-code-review.yml`: 上記に加え `issues: write` 付与。

### 含まないもの
- 認証シークレット（`CLAUDE_CODE_OAUTH_TOKEN` / `ANTHROPIC_API_KEY`）の設定。
- ワークフローの起動条件（`on:`）の変更。

## 受け入れ基準

- [x] `claude-code-review.yml` の `permissions` に `issues: write` がある。
- [x] 両ファイルの `--allowedTools` に `Bash(gh issue create:*)` がある。
- [x] 両ファイルの `Run Claude Code` ステップに `GH_TOKEN` env がある。
- [x] 両ファイルで `--model` が解析ステップ出力（`steps.model.outputs.model`）を参照する。
- [x] 解析ステップはコメント/PR 本文を `env:` 経由で受け取り、直接展開しない。
- [ ] （実地確認）`@claude opus` 指定時に CI ログの「Selected model」が opus になる。
- [ ] （実地確認）`@claude` に Issue 作成を依頼すると新規 Issue が作成される。

## リスク・注意事項

- `.github/workflows/` は GitHub App 権限（`workflows` スコープ）が無いと push 不可。
  本変更はローカルコミットし、push はメンテナ環境で行う。
- `gh` の認証は `GH_TOKEN`（=`GITHUB_TOKEN`）に依存する。トークン権限 `issues: write`
  が無いリポジトリでは作成が 403 になる。
- モデル解析は `@claude <model>` 形式のみ対応。未知の指定は既定にフォールバックする。
- **【AI 固有リスク】プロンプトインジェクション経由の Issue 乱発**:
  `claude-code-review.yml` は `pull_request: [opened, synchronize]` で**無条件・自動起動**
  し、レビューエージェントは PR 本文・差分・コード内容を読む。ここに `issues: write` と
  `--allowedTools Bash(gh issue create:*)` が付与されたため、悪意ある PR 内容
  （プロンプトインジェクション）によって意図しない `gh issue create` が誘発される
  余地がある。`@claude` 明示メンション時のみ起動する `claude-coding.yml` と異なり、
  こちらは外部からの PR でも自動起動する点に注意。
  - **緩和策（要検討）**: (a) review ジョブから Issue 作成権限を外し、Issue 発行は
    明示メンション起動の coding ジョブに限定する、(b) 作成した Issue に発行理由・
    トリガ元 PR を必ず記録して事後監査可能にする、(c) fork からの PR に対しては
    `issues: write` を付与しない（`pull_request_target` を使わない）運用を維持する。
  - **残存リスクの範囲（重要）**: `claude-code-review.yml` は `pull_request`
    （`pull_request_target` ではない）トリガのため、**フォーク元からの PR では GitHub
    側の既定動作により `GITHUB_TOKEN` が読み取り専用に強制される**。よって
    `issues: write` を付与していてもフォーク PR では実際には権限昇格せず、`gh issue create`
    は 403 になる。リスクが顕在化しうるのは、**同一リポジトリ内のブランチから PR を
    出せる（＝既に write 権限を持つ）関係者**に限られる。緩和策 (c) の
    「`pull_request_target` を使わない」運用維持は、この既定制限を保つうえで有効。
  - 現状は既定モデル・既定プロンプトのレビュー用途を想定しており即時の実害は
    確認されていないが、権限拡張に伴う残存リスクとして記録する。
- **【AI 固有リスク】モデル選択によるコスト増**:
  `claude-code-review.yml` の `Select model` ステップは、**無条件・自動起動**の
  レビュージョブでも PR 本文/タイトルの `@claude opus` 等を解析する。Issue 作成の
  ような権限昇格は伴わないが、**フォーク PR を含め誰でも PR 本文に `@claude opus` と
  書くだけで高コストなモデルへ切替**でき、意図しない実行コスト増を招く余地がある。
  - **緩和策（要検討）**: (a) 自動起動の review ジョブではモデル選択を無効化し既定
    モデル固定とする、(b) モデル選択を許可する場合も高コストモデルは同一リポジトリの
    関係者による明示メンション（coding ジョブ）に限定する、(c) 実行コストの上限
    アラート/予算監視を CI 側で設ける。
