# AI 駆動の実装ワークフロー（Runbook）

このリポジトリは **実装の大半を生成 AI に任せる**前提で構成している。本書は、計画書から実装・マージまでを AI 中心で回すための運用手順と、全自動化に有用なツールをまとめる。

## 全体フロー

```text
計画リポ(project-planning)
   │  gen-issues / 手動
   ▼
GitHub Issue（[ai-impl] / ai-implement ラベル）
   │  @claude メンション
   ▼
AI 実装（claude-code-action）
   ├─ 作業仕様書を作成（/new-spec）         ← 着手前に必須
   ├─ 必須仕様書・実装ADR を作成/更新
   ├─ ブランチ作成・実装・テスト（起点ID をトレース）
   └─ /verify（ビルド/テスト/lint・受け入れ基準照合）
   ▼
Pull Request
   ├─ AI 自動レビュー（claude-code-review）
   ├─ CI ゲート（ci: lint/build/test/coverage、security: gitleaks/dependency-review、CodeQL）
   └─ 人間レビュー（CODEOWNERS、AI特有リスクの確認）
   ▼
マージ → CHANGELOG / OpenAPI 自動更新
```

## 手順

### 0. 初期セットアップ（プロファイル選択 ＋ `.example` の有効化）

**最初に `AI_SETUP.md` で利用可能な AI（プロファイル）を宣言する。** プロファイルにより有効化するファイルとシークレットが変わる。`*.example` ファイルは拡張子から `.example` を外すと有効になる（GitHub Actions は `.github/workflows/*.yml` のみ実行する）。`scripts/apply-profile.sh` で自動化できる。

技術非依存の CI 系は全プロファイル共通で有効化する。

```bash
git mv .github/workflows/ci.example.yml     .github/workflows/ci.yml
git mv .github/workflows/codeql.example.yml .github/workflows/codeql.yml
```

`security.yml`・`changelog.yml`・`openapi.yml` はそのまま有効。ベンダー起動系はプロファイルで分岐する。

| プロファイル | 有効化するファイル | シークレット |
| --- | --- | --- |
| `claude-code`（サブスク） | `claude.example.yml` / `claude-code-review.example.yml` | `CLAUDE_CODE_OAUTH_TOKEN`（`claude setup-token`） |
| `api` | 同上 | `ANTHROPIC_API_KEY` |
| `copilot` | `copilot-setup-steps.example.yml` | （リポジトリで Copilot を有効化） |

```bash
# 例: Claude（サブスク or API）— apply-profile.sh が claude*.yml を有効化
bash scripts/apply-profile.sh claude-code   # or: api

# 例: GitHub Copilot
bash scripts/apply-profile.sh copilot
```

### 1. タスクを起票する

- 計画リポ側で `tools/impl-handoff-kit/generators/gen-issues.js` を実行し、要求/UC から Issue 下書き（JSON）を生成 → `gh` / GitHub MCP で起票。
- または「AI 実装タスク」テンプレート（`.github/ISSUE_TEMPLATE/ai-implementation.yml`）で起票。

### 2. AI に着手させる（プロファイル別）

- **Claude（サブスク / API）**: Issue / PR で `@claude このタスクを実装してください` とコメントする（`claude.yml` が応答）。
- **GitHub Copilot**: Issue を Copilot にアサインする（coding agent が `copilot-setup-steps.yml` の環境で起動）。
- いずれも AI は次を行う: 計画書を読む → 作業仕様書を作成 → 必須仕様書・実装ADR を整備 → 実装 → テスト → 検証（Claude は `/verify`、Copilot は CI / DoD）。

### 3. レビューとゲート

- PR を開くと AI 自動レビュー（`claude-code-review.yml`）が走る。
- CI（`ci.yml`）と セキュリティ（`security.yml` / `codeql.yml`）が green であることを必須にする。
- 人間は PR テンプレートの「レビュアー向け（AI実装の確認観点）」で最終確認する。

### 4. マージ後

- `changelog.yml` が `CHANGELOG.md` を、`openapi.yml` が `docs/api/openapi.yaml` を自動更新する。

## 全自動化のための推奨ツール・設定

| 目的 | ツール / 設定 | 備考 |
| --- | --- | --- |
| AI 実装の起動（Claude） | `anthropics/claude-code-action@v1`（`claude.yml` / `claude-code-review.yml`） | サブスク=`CLAUDE_CODE_OAUTH_TOKEN` / API=`ANTHROPIC_API_KEY` のいずれか |
| AI 実装の起動（Copilot） | Copilot coding agent（Issue 割当）＋ `copilot-setup-steps.yml` | リポジトリで Copilot を有効化 |
| 対話的に AI 実装 | Claude Code（CLI / Web / IDE）/ Copilot（IDE） | Web は SessionStart hook（`scripts/setup.sh`）で環境準備 |
| 再現可能な環境 | devcontainer / GitHub Codespaces（`.devcontainer/`） | AI がビルド・テストを実走できる |
| 暴走防止（ローカル） | `.claude/hooks/`（guard-bash / guard-secrets / check-impl）＋ `settings.json` の permissions | 破壊的操作・秘密情報・仕様書なし実装を抑止 |
| 品質ゲート | CI 必須チェック ＋ ブランチ保護 | 下記「必須チェックの有効化」 |
| 秘密情報 | gitleaks（`security.yml`）＋ `.gitignore`（`.env` 等） | 鍵の混入・コミットを防ぐ |
| 脆弱性 | dependency-review（`security.yml`）＋ CodeQL ＋ Dependabot | 供給網・SAST |
| 完了の定義 | `docs/DEFINITION_OF_DONE.md` ＋ `/verify` | AI 自身の完了前検証 |
| トレーサビリティ | `/trace-check`・`/adr-check`・`.claude/rules/traceability.md` | 計画と実装の整合 |
| 計画への環流 | `/plan-feedback`（実装→計画） | 計画書の誤り・不足を戻す |

### 必須チェックの有効化（人手の検証を最小化する要）

GitHub の **ブランチ保護ルール**（Settings → Branches → Add rule）で以下を推奨設定する。

- Require a pull request before merging（直接 push 禁止）
- Require status checks to pass before merging → `CI`・`Security`・`CodeQL` を必須に
- Require review from Code Owners（`CODEOWNERS` を配置）
- Require conversation resolution before merging

これにより、AI が作成した PR も「機械チェック green ＋ 必要なレビュー承認」を満たさない限りマージされない。

## 安全に任せるための原則

- AI は**着手前に作業仕様書を作成**し、それに沿って実装する（hook が警告）。
- 破壊的操作・秘密情報コミットは hook と権限設定でブロックする。
- マージ前に **CI ゲート ＋ 人間の最終レビュー** を必ず通す（全自動でも最後の人間ゲートは残す）。
- 計画書に反する判断は実装で押し通さず、`/plan-feedback` で計画側へ戻す。
