<!-- trace:
adrs: [ADR-0048]
iadrs: [IADR-0067, IADR-0180, IADR-0240]
issues: [#268, #719, #783, #1019, planning#286]
-->

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

**初回チェックリスト**（着手前に上から順に確認する）:

- [ ] repo-template の中身をこのリポジトリ直下にコピー済みである（`.claude/` `.github/` `docs/` など）。
- [ ] 計画リポ（`project-planning`）を参照できる（**本リポは planning に依存しない**）。GitHub 上の URL または隣接クローン（既定 `../project-planning`）で計画書の該当 ID を開いて確認する。
- [ ] `AI_SETUP.md` で利用可能な AI を宣言し、`bash scripts/apply-profile.sh <profile>` を実行済みである。
- [ ] CI 系を有効化済みである（`ci.example.yml` / `codeql.example.yml` の `.example` を外す）。
- [ ] GitHub Secrets（`CLAUDE_CODE_OAUTH_TOKEN` か `ANTHROPIC_API_KEY`）を登録済みである（Copilot 利用時はリポジトリで Copilot を有効化）。
- [ ] 環境セットアップ（`scripts/setup.sh`）が通り、ビルド・テストが実走できる。

**最初に `AI_SETUP.md` で利用可能な AI（プロファイル）を宣言する。** プロファイルにより有効化するファイルとシークレットが変わる。
本書の Claude 系ワークフローは役割スロット（orchestrator / worker / reviewer）の**既定エンジン実装**であり、エンジンの差し替え・フォールバックは `ai-roster.json` と [`docs/ai-orchestration.md`](ai-orchestration.md)（正本）に従う。
`*.example` ファイルは拡張子から `.example` を外すと有効になる（GitHub Actions は `.github/workflows/*.yml` のみ実行する）。`scripts/apply-profile.sh` で自動化できる。

技術非依存の CI 系は全プロファイル共通で有効化する。

```bash
git mv .github/workflows/ci.example.yml     .github/workflows/ci.yml
git mv .github/workflows/codeql.example.yml .github/workflows/codeql.yml
```

`security.yml`・`changelog.yml`・`openapi.yml` はそのまま有効。ベンダー起動系はプロファイルで分岐する。

| プロファイル | 有効化するファイル | シークレット |
| --- | --- | --- |
| `claude-code`（サブスク） | `claude-coding.example.yml` / `claude-code-review.example.yml` | `CLAUDE_CODE_OAUTH_TOKEN`（`claude setup-token`） |
| `api` | 同上 | `ANTHROPIC_API_KEY` |
| `copilot` | `copilot-setup-steps.example.yml` | （リポジトリで Copilot を有効化） |

```bash
# 例: Claude（サブスク or API）— apply-profile.sh が claude*.yml を有効化
bash scripts/apply-profile.sh claude-code   # or: api

# 例: GitHub Copilot
bash scripts/apply-profile.sh copilot
```

### 1. タスクを起票する

- 計画リポ側で `/handoff <project>`（または `node tools/impl-handoff-kit/generators/handoff.js <project>`）を実行し、`ai-context/<project>/issues.json` を生成 → `gh` / GitHub MCP で起票。
- または「AI 実装タスク」テンプレート（`.github/ISSUE_TEMPLATE/ai-implementation.yml`）で起票。

### 2. AI に着手させる（プロファイル別）

- **Claude（サブスク / API）**: Issue / PR で `@claude このタスクを実装してください` とコメントする（`claude-coding.yml` が応答）。
- **GitHub Copilot**: Issue を Copilot にアサインする（coding agent が `copilot-setup-steps.yml` の環境で起動）。
- いずれも AI は次を行う: 計画書を読む → 作業仕様書を作成 → 必須仕様書・実装ADR を整備 → 実装 → テスト → 検証（Claude は `/verify`、Copilot は CI / DoD）。

### 3. レビューとゲート

- PR を開くと AI 自動レビュー（`claude-code-review.yml`）が走る。
- CI（`ci.yml`）・イメージビルド（`images.yml`）・PR タイトル（`pr-title.yml`）・セキュリティ（`security.yml`）が green であることを必須にする
  （**`codeql.yml` は `paths:` を持つため必須にしない** —— 後述の必須チェック表を参照）。
  `pr-title.yml` はスカッシュ後件名の唯一の予防線であり（中間コミットは force push 禁止で事後修正できない）、
  全 PR で起動するため必須チェックに指定してよい（後述「必須チェックに指定する際の注意」）。
- 人間は PR テンプレートの「レビュアー向け（AI実装の確認観点）」で最終確認する。

### 4. マージ後

- `changelog.yml` が `CHANGELOG.md` を、`openapi.yml` が `docs/api/openapi.yaml` を自動更新する。

## 全自動化のための推奨ツール・設定

| 目的 | ツール / 設定 | 備考 |
| --- | --- | --- |
| AI 実装の起動（Claude） | `anthropics/claude-code-action@v1`（`claude-coding.yml` / `claude-code-review.yml`） | サブスク=`CLAUDE_CODE_OAUTH_TOKEN` / API=`ANTHROPIC_API_KEY` のいずれか |
| AI 実装の起動（Copilot） | Copilot coding agent（Issue 割当）＋ `copilot-setup-steps.yml` | リポジトリで Copilot を有効化 |
| 対話的に AI 実装 | Claude Code（CLI / Web / IDE）/ Copilot（IDE） | Web は SessionStart hook（`scripts/setup.sh`）で環境準備 |
| 再現可能な環境 | devcontainer / GitHub Codespaces（`.devcontainer/`） | AI がビルド・テストを実走できる |
| 暴走防止（ローカル） | `.claude/hooks/`（guard-bash / guard-secrets / check-impl）＋ `settings.json` の permissions | 破壊的操作・秘密情報・仕様書なし実装を抑止 |
| 品質ゲート | CI 必須チェック ＋ ブランチ保護 | 下記「必須チェックの有効化」 |
| 秘密情報 | gitleaks（`security.yml`）＋ `.gitignore`（`.env` 等） | 鍵の混入・コミットを防ぐ |
| 脆弱性 | dependency-review（`security.yml`）＋ CodeQL ＋ Dependabot | 供給網・SAST |
| 完了の定義 | `docs/DEFINITION_OF_DONE.md` ＋ `/verify` | AI 自身の完了前検証 |
| トレーサビリティ | `/trace-check`・`/adr-check`・`.claude/rules/traceability.md` | 計画と実装の整合 |
| `docs/` の非表示メタデータ | `scripts/check-trace-blocks.js`・`scripts/gen-knowledge-graph.js` | trace ブロックの文法・値域・可視本文への ID 残存（CI の `doc-links` ジョブ） |
| 計画への環流 | `/plan-feedback`（実装→計画） | 計画リポジトリへ GitHub issue で起票する（本リポジトリに記録ファイルは残さない） |

### 必須チェックの有効化（人手の検証を最小化する要）

> **★［2026-08-30 更新 / #936］配備した。** 本節は「推奨設定」ではなく**現在の設定**である。
> 下表の 7 件を必須チェックにし、`enforce_admins` を有効にした（**管理者も迂回できない**）。
> 直前まで、この節は「推奨設定であって現在そうなっているではない」と断っていた。
>
> | 設定 | 値 | 理由 |
> | --- | --- | --- |
> | 必須チェック | 下表の 7 件 | いずれも `paths:` を持たず `reopened` を含む＝**全 PR で起動する**ことを実測で確認した |
> | `enforce_admins` | **`true`** | `false` だと「赤いままマージを打てば通る」が残る。このリポジトリの操作主体は管理者権限を持つため、**`false` では統制にならない** |
> | `required_pull_request_reviews` | **`null`**（承認必須にしない） | 🔴 **下の推奨（Code Owners レビュー必須）から意図的に外れる。** 人間が 1 人であり、承認必須にすると**全 PR がその人の手作業待ち**になって流れが止まる。#936 の主題は「CI が機械的に強制されていない」ことであり、レビュー要件は別の政策判断である |
> | `strict` | `false` | 強制すると、待ち行列の全 PR が 1 本着地するたびに base 取り込みと CI 再走を強いられる。FIFO の規律は運用側が持つ |
>
> **リスクを 1 つ受け入れている**: `claude-review` を必須にしたため、**AI レビューの実行基盤が落ちると
> 全 PR がマージ不能になる**。解除は管理者の API 1 回（`gh api -X PUT .../protection`）で戻せる。

GitHub の **ブランチ保護ルール**（Settings → Branches → Add rule）で以下を推奨設定する。

- Require a pull request before merging（直接 push 禁止）
- Require status checks to pass before merging → **下表の check 名**を必須に
- Require review from Code Owners（`CODEOWNERS` を配置）
- Require conversation resolution before merging

#### ★ 指定するのは「check の名前」であって「ワークフローの名前」ではない

**ここを取り違えると develop が恒久的にマージ不能になる。**
GitHub Actions が report する status check の context は**ジョブ側の名前**であり、
`ci.yml` の `name: CI` のような**ワークフロー名は context として存在しない**。
存在しない context を必須に指定すると、**永久に pending のままマージできなくなる。**

| 必須にする check 名 | 出所 | 備考 |
| --- | --- | --- |
| `build-and-test` | `ci.yml` | ビルドとテスト。**全 PR で起動する** |
| `lint` | `ci.yml` | `dotnet format --verify-no-changes` ほか |
| `commit-messages` | `ci.yml` | 件名規約（スカッシュ前の中間コミット） |
| `pr-title` | `pr-title.yml` | スカッシュ後件名の唯一の予防線 |
| `image-build` | `images.yml` | サービスイメージのビルド検証（compose を単一情報源とする独立ワークフロー）の集約ジョブ |
| `scripts-tests` | `ci.yml` | 🔴 **#936 で追加。** 検査器そのものの単体試験（`scripts.test.js` の 664 件）と、**`check-adr-numbering` / `check-doc-updated` / `check-landed-subjects` の実データ判定**がここで走る。`paths:` を持たず全 PR で起動し、matrix でもない。**これを必須にしないと、採番の欠番・`updated:` の据え置き・着地件名の規約違反が赤いまま着地できる**（#936 の作業中に前 2 者が実際に赤くなった） |
| `static-checks-units` | `ci.yml` | submodule 取得が要る静的検査の集約ジョブ（unit 依存方向・chart / overlay のレンダリング＋スキーマ突合・unit サービス所有権）。`paths:` を持たず全 PR で起動し、matrix でもない |
| ~~`CodeQL`~~ | `codeql.yml` | **必須にしない（#719 で除外へ変更）**。`pull_request` に `paths:` を持つため、コード変更の無い PR では check 自体が report されず、必須指定すると恒久 pending になる。集約 check 名 `CodeQL`（ジョブ名 `Analyze (csharp)` と別物）である点は従来どおり。網羅は push（develop/main）と週次 schedule の全量解析が担保する |
| `claude-review` | `claude-code-review.yml` | **完了**を担保する（後述の注意を必ず読むこと） |

> **★ `CI` / `Security` / `Images` / `PR Title` を指定してはならない。** いずれも**ワークフロー名**であり、
> **check としては存在しない**（PR #704 が report した check 名 28 件を全数で突き合わせて確認・2026-08-11）。
> `security.yml` を必須にしたい場合は、ジョブの表示名 `Secret scan (gitleaks)` /
> `Dependency review` / `Vulnerable transitive dependencies` を個別に指定する。

#### ★ `claude-review` を必須にする場合の注意

- **担保できるのは「レビューが完走したこと」だけで、「指摘が無いこと」ではない。**
  AI レビューは **🔴 の指摘があっても success を返す**（採否の判断は人が行う）。
  **必須にしても 🔴 のままのマージは止まらない。**
- **必須にする前に、`types:` に `reopened` があることを確認する。** 無いと、再オープンされた PR で
  check が report されず**永久 pending**になる（#705 で是正済み。**回帰テストで固定している**）。
- AI の実行基盤が落ちている間は**全 PR がマージ不能**になる。トークン失効・レート超過も同じで、
  止めてよい範囲かを決めてから必須にすること。

#### 必須チェックに指定する際の注意

- **「その PR で起動しないことがある」チェックを必須にしてはならない。** GitHub は必須チェックが
  report されるまでマージを許さないため、**起動しなければ永久に pending のままマージ不能**になる。
  実際に踏みうる原因は 2 つあり、**どちらも結果は同じ**である。
  1. **`paths:` フィルタ** —— 対象パスに触れない PR では起動しない。デプロイ用・フロントエンド用など
     特定ディレクトリだけを対象にするワークフローが該当する（`frontend.yml` 等は**意図してそう設定して
     おり、必須にしないことで正しく運用されている**）。
  2. **`types:` の取りこぼし** —— `reopened` が無いワークフローは、再オープンされた PR で起動しない。
     **`pull_request` で起動する全ワークフローが `reopened` を含むことを回帰テストで固定している**（#705）。
- **`pr-title.yml` は必須チェックに指定してよい。** 全 PR で起動し、かつスカッシュ後件名の唯一の
  予防線である（中間コミットは force push 禁止で事後修正できない）。
- **マトリクスジョブ（`build (<service>)`）は指定しない。** 対象の増減で名前が変わる。
  集約ジョブ（`image-build`）を指定する。
- **bot 作成 PR で `if:` によりジョブごとスキップされたチェックは、必須チェック上「合格」として扱われる**
  ためマージは止まらない。bot を除外する条件を書いてもブランチ保護と矛盾しない。

#### API から設定する場合

UI と等価の設定は REST でも行える（`admin:repo` 相当の権限が要る）。

```console
$ gh api -X PUT repos/<owner>/<repo>/branches/develop/protection \
    --input protection.json
```

`protection.json` の `required_status_checks.contexts` に**上表の check 名をそのまま並べる**
（ワークフロー名を書かないこと）。`strict: true` にすると base の最新化も要求する。

#### 設定を AI ができるかは環境で変わる（最後の測定: 2026-08-30 / #936）

**［2026-08-30 更新］3 点のうち 2 点が変わり、AI が設定できるようになった。** 本節の手順で実際に配備した。
**旧記述（2026-08-11 / #705）は「本リポの実装セッションからは設定を変更できない」と結論していた。**
結論だけ差し替えず、**何がどう変わったか**を残す —— 環境が戻れば結論も戻るためである。

| 経路 | 2026-08-11 | **2026-08-30（再測定）** | 種別 |
| --- | --- | --- | --- |
| MCP の GitHub ツール | branch protection / ruleset を扱うツールが**無い** | **変わらず無い**（`ToolSearch` で `branch protection` / `ruleset` / `required status` を引き、返った 6 件のいずれも該当せず） | **能力の不在** |
| `gh` / `hub` CLI | **どちらも入っていない** | 🔴 **`gh` が入った**（`/c/Program Files/GitHub CLI/gh`・`gh version 2.95.0`）。`hub` は引き続き無い | **能力の不在 → 解消** |
| GitHub API を直接叩く | **セッション指示が禁じている**（GitHub 操作は MCP ツール経由に限る） | 🔴 **禁じられていない。** さらに本作業では**利用者が明示的に許可した** | **規則による禁止 → 解消** |

**能力の不在は環境が変われば消えるが、規則の禁止は指示が変わらない限り残る。**
**混ぜて「できない」と書かない** —— この書き分けがあったおかげで、
再測定で「どちらの理由が消えたのか」を項目ごとに言えている。

**棚卸しのたびに測り直す**こと。再測定の手順:

1. `ToolSearch` で `branch protection` / `ruleset` / `required status` を引き、**MCP ツールの有無を全数で確認**する
2. `command -v gh hub` で CLI の有無を見る
3. セッション指示が GitHub API の直接利用を許しているか読み直す

**3 点とも塞がっている間は、設定は人が行う**（本節の手順をそのまま渡せばよい）。
**塞がっていない場合でも、`enforce_admins` や必須 check の増減は運用の形を変えるため、利用者の同意を取ってから行う。**

### 検査器の配線・CHANGELOG の是正（別紙）

**規約の本文は [`.claude/rules/traceability.md`](../.claude/rules/traceability.md)、配線と運用の詳細は
[`docs/traceability-appendix.md`](traceability-appendix.md) が持つ。**
本書は技術スタック固有の CI 配線を扱うため配布先ごとに差分を持つ。**［2026-08-21 変更］別紙も
キットとのバイト一致を前提としない** —— 資料再編の計画 ADR 決定 6 でキットは bootstrap 専用となり、
バイト一致の同期検査は退役した。別紙は本リポジトリ固有の節（`docs/` の trace ブロック等）を持つ。

## よくある詰まり（FAQ）

| 症状 | 対処 |
| --- | --- |
| スラッシュコマンド（`/new-spec` 等）が出ない | repo-template の `.claude/` をリポ直下にコピーしたか確認し、Claude Code を再起動して読み直す。 |
| 計画書（`projects/<name>`）を参照できない | 本リポは planning に依存しない。隣接クローン（既定 `../project-planning`）を用意するか、GitHub 上の URL で該当ページを開いて確認する。 |
| CI / AI ワークフローが起動しない | `.example` を外して有効化したか（`scripts/apply-profile.sh`）、必要な Secrets を登録したか確認する。Actions のログでトリガ条件を確認する。 |
| `@claude` が反応しない | `claude-coding.yml` が有効化済みか、`CLAUDE_CODE_OAUTH_TOKEN` か `ANTHROPIC_API_KEY` のいずれかが登録済みかを確認する。 |
| ビルド・テストが C#/.NET 前提で合わない | 技術スタック別の差し替え対象（`ci.yml` / `setup.sh` / `.devcontainer/` / `settings.json` の permissions）を使用言語へ直す。一覧は計画リポの `tools/impl-handoff-kit/README.md`「技術スタック別の差し替え対象」。 |

## 安全に任せるための原則

- AI は**着手前に作業仕様書を作成**し、それに沿って実装する（hook が警告）。
- 破壊的操作・秘密情報コミットは hook と権限設定でブロックする。
- マージ前に **CI ゲート ＋ 人間の最終レビュー** を必ず通す（全自動でも最後の人間ゲートは残す）。
- 計画書に反する判断は実装で押し通さず、計画リポジトリへ GitHub issue で戻す（`/plan-feedback`）。
