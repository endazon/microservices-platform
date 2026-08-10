---
title: Dependabot/submodule トークン配線の失敗修正（Issue #260 マージ後・3 リポ横断最終タスク）
type: spec
status: done
related_ids:
  - FR-14
  - NFR
  - IADR-0058
  - IADR-0060
  - IADR-0065
author: claude
created: 2026-07-12
updated: 2026-07-12
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-14: 構成変更で完結する疎結合ユニット。ユニット submodule の pin 鮮度維持が関連)"
related_specs:
  - "20260712_issue-260_dependabot-gitsubmodule.md"
  - "../adr/IADR-0058_doc-links-planning-submodule-ci.md"
  - "../adr/IADR-0060_submodule-unit-operations.md"
  - "../adr/IADR-0065_public-unit-submodule-ci-fetch-no-token.md"
  - "../../.github/dependabot.yml"
  - "../../.github/workflows/claude-code-review.yml"
  - "../../.github/workflows/claude-coding.yml"
---

# 作業仕様書: Dependabot/submodule トークン配線の失敗修正（Issue #260 マージ後）

## 起点となる計画書・Issue（トレーサビリティ）

- Issue: #260（`docs/specs/20260712_issue-260_dependabot-gitsubmodule.md` の「private planning への
  Dependabot アクセス（マージ後の確認事項）」節で予告されていた確認作業。`Refs #260`）
- 横断ハンドオフ: planning#24（repo-template 根本対応）／AST#112（AST 側修正）。
  本作業はその 3 リポ横断修正の最終タスク（microservices-platform 側）。
- 関連 IADR: [[IADR-0058]]（private submodule の CI 取得はトークン付き）／[[IADR-0060]]（submodule 運用）／
  [[IADR-0065]]（public ユニットの CI 取得はトークン不要・`src/*` 非再帰 init）

## 目的・背景

Issue #260 の初期実装（`.github/dependabot.yml` への `gitsubmodule` ブロック追加）マージ後、以下 2 件の
失敗が判明した。原因はいずれも「private な `planning`（`endazon/project-planning`）へ自動化がアクセスする
トークンの配線」に起因する。

- **Failure 1**: Dependabot が `planning` submodule の pin 更新を `git_dependencies_not_reachable` で
  更新失敗。Dependabot は認証情報を `dependabot.yml` の `registries` 経由でのみ受け取れる仕組みであり、
  `directory: "/"` を指定しただけでは private submodule への認証手段がなかった。
- **Failure 2**: Dependabot が生成した pin 更新 PR（microservices-platform#262）上で `claude-review`
  ワークフローが失敗。原因は 2 つ複合していた。
  1. `claude-code-review.yml` / `claude-coding.yml` が参照する `SUBMODULE_ACCESS_PAT`（Actions secret）が
     Dependabot PR には注入されない（Dependabot はセキュリティ上 Actions secret にアクセスできず、
     Dependabot secret のみ参照可能）。
  2. 機械的な pin bump PR に AI レビューは不要であり、そもそも起動する必要がない。

オーナー確認済みの方針（2026-07-12）: `PLANNING_REPO_TOKEN` を Actions secret 兼 Dependabot secret として
新規登録済み。旧 `SUBMODULE_ACCESS_PAT`（Actions secret のみ）は廃止予定とし、全ワークフロー参照を
`PLANNING_REPO_TOKEN` に統一する（secret 自体の削除はオーナーがマージ後に実施）。

## 実装内容

### 1. `.github/dependabot.yml` — `registries` 追加 + `gitsubmodule` への紐付け

`version: 2` の直後に `registries` ブロックを追加し、既存の `gitsubmodule` update に
`registries: [planning-git]` を追加する。private 権限に関するコメントを
`PLANNING_REPO_TOKEN`/`registries` 方式の説明に更新する。

```yaml
version: 2

registries:
  planning-git:
    type: git
    url: https://github.com
    username: x-access-token
    password: ${{secrets.PLANNING_REPO_TOKEN}}

updates:
  - package-ecosystem: "github-actions"
    directory: "/"
    schedule:
      interval: "weekly"

  # NFR: submodule の pin 自動更新（gitlink を追跡先の先端へ前進させる更新 PR を生成する）。
  # 既定は自動マージしない（人手レビュー必須）。root .gitmodules の全 submodule が対象
  # （planning=private の project-planning を含む）。private submodule の更新には
  # Dependabot secret の PLANNING_REPO_TOKEN を registries(planning-git) 経由で使う。
  # 特定 submodule だけ除外するなら ignore の dependency-name にそのパスを指定する。
  - package-ecosystem: "gitsubmodule"
    directory: "/"
    registries:
      - planning-git
    schedule:
      interval: "weekly"
    open-pull-requests-limit: 5
```

`registries` は Dependabot の「認証情報」を定義するものであり、対象 submodule のパスとは無関係。
`gitsubmodule` update 1 個に `registries: [planning-git]` を紐付ければ、root `.gitmodules` に列挙された
両方の submodule（`planning` と `src/ai-stock-trading`）の更新チェックに同じ認証情報が使われる。
`src/ai-stock-trading` は public なので認証不要のまま通り、`planning` のみ `PLANNING_REPO_TOKEN` で
認証される。submodule ごとに `package-ecosystem: "gitsubmodule"` ブロックを分ける必要はない。
`planning` を除外する `ignore` は入れない（当初方針どおり、`planning` pin も自動更新対象に含める。
`docs/specs/20260712_issue-260_dependabot-gitsubmodule.md` の「当初案からの変更点と理由」を踏襲）。

#### リスクとフォールバック（registries × gitsubmodule）

`registries`（`type: git`）は GitHub 公式ドキュメントに定義された private アクセス機構だが、`gitsubmodule`
エコシステムがこの認証を実際に消費するかは、マージ後の Dependabot 実行で確認する（本 PR 時点では未実証・
外部検証不可）。**マージ後、Dependabot Insights/ログで `planning` の pin 更新 PR 生成が成功するか実地確認する。**
万一 `gitsubmodule` が `registries` を消費せず `git_dependencies_not_reachable` が再発する場合のフォールバックは、
`gitsubmodule` update に `ignore` で `dependency-name: "planning"` を追加して **`planning` を Dependabot 対象外**に
する（`planning` pin は従来どおり手動更新に戻す）。この場合も `src/ai-stock-trading`（public）の自動更新は
そのまま機能する。fail-safe（失敗しても CI・他 PR のマージ可否には影響しない）である点は変わらない。

### 2. `.github/workflows/claude-code-review.yml`

1. トークン参照の統一: `env.SUBMODULE_PAT` の値を `${{ secrets.SUBMODULE_ACCESS_PAT }}` から
   `${{ secrets.PLANNING_REPO_TOKEN }}` に変更し、直上のコメントの `SUBMODULE_ACCESS_PAT` を
   `PLANNING_REPO_TOKEN` に直す。
2. `claude-review` ジョブに Dependabot PR でのスキップ条件を追加:
   ```yaml
   jobs:
     claude-review:
       if: ${{ github.actor != 'dependabot[bot]' }}
       runs-on: ubuntu-latest
   ```
   機械的な pin bump に AI レビューは不要。本リポにブランチ保護による必須チェック指定は無いため、
   スキップしても他 PR のマージ可否に影響しない。

### 3. `.github/workflows/claude-coding.yml`

トークン参照の統一のみ（`env.SUBMODULE_PAT` を `PLANNING_REPO_TOKEN` に、直上コメントも同様に修正）。
`claude-coding.yml` は `issue_comment` 等の対話イベントでのみ起動し Dependabot PR の自動トリガ対象では
ないため、スキップ条件の追加は不要。

## fail-safe / 制約

- `dependabot.yml` に auto-merge 設定は追加しない（pin 更新は必ず人手レビュー・PR 経由）。
- `SUBMODULE_ACCESS_PAT` という文字列は本 PR 適用後、`.github/` 配下の実ファイル
  （`claude-code-review.yml` / `claude-coding.yml`）からは除去される。`planning/` および
  `src/ai-stock-trading/` 配下（submodule として取り込んだ他リポのファイル）に同名の参照が残るのは
  正常（それぞれ planning#24 / AST#112 で別途対応済み・別リポのスコープ）。
- **secret 自体（`SUBMODULE_ACCESS_PAT`）の削除はオーナーが実施する**（本 PR では参照を切り替えるのみ。
  値は一切コミットしない）。
- `develop` への直接 push・マージは行わない。PR 経由。`claude-review` の指摘対応は本作業のスコープ外
  （メインセッションが担当）。

## 検証

- YAML 構文: `npx --yes js-yaml .github/dependabot.yml` / `.github/workflows/claude-code-review.yml` /
  `.github/workflows/claude-coding.yml`（個別に実行）。
- コミット規約: `GITHUB_BASE_REF=develop node scripts/check-commit-messages.js --verbose`
- doc-links: `node scripts/check-doc-links.js`
- PR 作成後、非レビュー系 CI（commit-messages / pr-title / doc-links / ci / security 等）の green化を
  確認する。可能であれば Dependabot 実 PR（#262）上で `claude-review` が実際にスキップされるかを実地
  確認する。claude-review 自体の指摘対応は本作業のスコープ外。

## 受け入れ基準

- [x] `.github/dependabot.yml` に `registries.planning-git` を追加し、`gitsubmodule` update に
      `registries: [planning-git]` を紐付けた。`planning` を除外する `ignore` は追加していない。
- [x] `.github/workflows/claude-code-review.yml` / `claude-coding.yml` の `SUBMODULE_PAT` 参照を
      `secrets.PLANNING_REPO_TOKEN` に統一した（コメントも追随）。
- [x] `claude-review` ジョブに `if: ${{ github.actor != 'dependabot[bot]' }}` を追加した。
- [x] `SUBMODULE_ACCESS_PAT` の値・その他秘密情報の値をコミットしていない（secret 名の参照のみ）。
- [ ] Dependabot の週次実行で `planning` submodule の pin 更新 PR が実際に生成されることの実証は
      本 PR のスコープ外（マージ後、次回の Dependabot 実行を待って確認）。
- [ ] 既存の Dependabot PR（#262）上で `claude-review` が実際にスキップされることの実地確認は、
      本 PR マージ後に別途確認する（マージ前は #262 のワークフローは旧定義のまま実行されるため）。
