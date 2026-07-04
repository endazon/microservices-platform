---
title: 作業仕様書 — CI・補助成果物ワークフローの develop 運用整合
type: work-spec
status: in-progress
related_ids:
  - NFR
author: claude
created: 2026-07-04
updated: 2026-07-04
plan_refs:
  - "../../CLAUDE.md（補助成果物の自動生成 / 自動化・検証・安全 / CI ゲート）"
related_specs:
  - ../operations/operations.md
  - ../tech/tech-requirements.md
related_adrs: []
issue: "#60"
parent_issue: "#48"
related_issues:
  - "#33"
  - "#34"
  - "#35"
---

# 作業仕様書: CI・補助成果物ワークフローの develop 運用整合

## 目的

既定ブランチが `develop`（`main` は Initial commit のまま）である一方、自動化ワークフローの
`push` / `pull_request` トリガーが `branches: [main]` に限定されているため、複数の CI ゲートと
補助成果物が機能していない。本作業でこの乖離を解消し、`develop` 運用でも CI・補助成果物・SAST が
正しく発火する状態にする。あわせてコミットメッセージ規約の機械チェックを CI へ追加し再発を防ぐ。

起点: NFR（CI/CD）、CLAUDE.md「補助成果物の自動生成」「自動化・検証・安全（CI ゲート）」。

## 権限に関する注記（重要）

本 PR を作成した Claude GitHub App は **`.github/workflows/` 配下および `.claude/` 配下を変更できない**
（編集時に権限エラーで拒否される）。そのため本 PR では App が編集可能な範囲（`scripts/` と `docs/`）を
実装し、ワークフロー・ルールの差分は本仕様書「§ 適用が必要な差分（人手）」に完全なパッチとして提示する。
これらは **workflows 権限を持つ人手での適用**、または別 sub issue で対応する。

### 本 PR で実装したもの（App が編集可能な範囲）

- **`scripts/check-commit-messages.js`（新規）** — コミット規約 `種別(起点ID): 要約` の機械チェック。
  - 検査範囲は `origin/$GITHUB_BASE_REF..HEAD`（PR）→ `origin/develop..HEAD` の順で決定。**既存履歴は書き換えず** PR 追加分のみ検査。
  - 除外: bot 著者（`dependabot[bot]`/`renovate[bot]`/`github-actions[bot]` 等）・マージコミット・`[skip ci]`・`Revert "..."`。
  - 種別集合は `gen-changelog.js` と一致。外部依存ゼロ。正常/違反サンプルで OK/NG を検証済み。
- **`scripts/gen-changelog.js`（改修）＋ `scripts/changelog-overrides.json`（新規）** — 誤記コミットの CHANGELOG 補正機構。
  - `git` 履歴は書き換えず、生成時のみ `changelog-overrides.json` の `overrides[]` に基づき補正/除外する。
  - `b421761`（件名 `feat(FR-10)` は誤記・実体は P0 骨格 `docs/specs/20260626_P0_infrastructure-skeleton.md`）を
    `docs(P0): P0 基盤スケルトン整備…` に **remap**（FR-10 誤帰属を解消）。前方一致で短縮/完全 SHA の双方に対応。
- **本仕様書（`docs/specs/`）** — CLAUDE.md 必須の作業仕様書。

## 現状分析（確認済みの実害）

| 対象 | 現状トリガー | 問題 |
| --- | --- | --- |
| `ci.yml` | `push: [main]` ＋ `pull_request`（全ブランチ） | PR 単位の CI は動くが、develop へのマージコミット自体は push で検証されない |
| `changelog.yml` | `push: [main]` ＋ tags | `CHANGELOG.md` が初期状態のまま未生成 |
| `openapi.yml` | `push: [main]` ＋ paths | develop への API 変更で発火しない |
| `codeql.yml` | `push:[main]` / `pull_request:[main]` / weekly | develop 向け PR で発火せず、FR-09〜FR-13 マージ分が未解析。最終解析 2026-06-29 |

### openapi.yaml の追加的な注意（破壊リスク）

`docs/api/openapi.yaml` は **手書きの OpenAPI 3.1.0 リッチ仕様**であり、`docs/api/` に生成元となる
通信仕様書（`*.md` のエンドポイント一覧表）は存在しない。この状態で `openapi.yml` が発火すると、
`generate-openapi.sh` も `OPENAPI_GENERATE_CMD` も未設定のため `scripts/gen-openapi-skeleton.js --force`
が実行され、**リッチな手書き仕様が 3.0.3 の空雛形で上書き破壊される**。したがって:

- openapi.yaml の「再生成」は機械実行しない。FR-08 / FR-10 / FR-11 の未反映 API は**手書き仕様への追記**で対応する（別 sub issue）。
- 併せて `openapi.yml` の雛形フォールバック（`--force`）が手書き仕様を破壊しないようガードする（下記差分 §3）。

## 対応方針の確定

### ブランチ運用（方針: (a) develop をトリガーへ追加）

CLAUDE.md は「`main` を安定版とする」とするが、実運用の既定ブランチは `develop`。二重管理を避けるため、
本作業では **(a) 各ワークフローのトリガーへ `develop` を追加**する方針を採る（(b) 定期リリースマージ運用は
別途 `operations.md` で確立する将来課題）。補助成果物の自動コミット先も `develop` になる点に留意する。

## 適用が必要な差分（人手 / 別 sub issue）

> `.github/workflows/` と `.claude/` は App 権限で編集不可のため、以下は workflows 権限を持つ人手での適用が必要。

### 1. `ci.yml`（push を develop 起点に ＋ commit-messages ジョブ追加）

```diff
 on:
   push:
-    branches: [main]
+    branches: [develop, main]
   pull_request:
     types: [opened, synchronize, reopened]

 jobs:
+  # コミットメッセージ規約（種別(起点ID): 要約）の機械チェック（Issue #60・再発防止）。
+  # PR で追加されるコミット（base..HEAD）のみ検査し、bot・マージ・[skip ci] は除外する。
+  # fetch-depth: 0 は base..HEAD の範囲解決に必須。
+  commit-messages:
+    runs-on: ubuntu-latest
+    if: github.event_name == 'pull_request'
+    steps:
+      - uses: actions/checkout@v7
+        with:
+          fetch-depth: 0
+      - uses: actions/setup-node@v6
+        with:
+          node-version: "20"
+      - name: Check commit messages
+        env:
+          GITHUB_BASE_REF: ${{ github.base_ref }}
+        run: node scripts/check-commit-messages.js
+
   # スタック非依存: docs/ 配下 Markdown の相対リンク切れを検査（Issue #59 再発防止）。
   doc-links:
```

### 2. `changelog.yml`（develop で CHANGELOG を再生成）

```diff
 on:
   push:
-    branches: [main]
+    branches: [develop, main]
     tags: ["v*"]
```

### 3. `openapi.yml`（develop 追加 ＋ 破壊防止ガード）

```diff
 on:
   push:
-    branches: [main]
+    branches: [develop, main]
     paths:
       - "docs/api/**"
       - "scripts/generate-openapi.sh"
@@
           else
-            echo "生成コマンド未設定。通信仕様書から雛形を生成する。"
-            node scripts/gen-openapi-skeleton.js --src docs/api --out docs/api/openapi.yaml --force
+            echo "生成コマンド未設定かつ手書き仕様を尊重。雛形上書きはスキップする。"
+            # docs/api/openapi.yaml は手書きの OpenAPI 3.1.0 リッチ仕様であり、生成元の
+            # 通信仕様書が存在しない。--force を付けると空雛形で上書き破壊されるため付けない
+            # （既存があれば上書きしない。Issue #60）。
+            node scripts/gen-openapi-skeleton.js --src docs/api --out docs/api/openapi.yaml || true
           fi
```

### 4. `codeql.yml`（develop 向け PR で発火）

```diff
 on:
   push:
-    branches: [main]
+    branches: [develop, main]
   pull_request:
-    branches: [main]
+    branches: [develop, main]
   schedule:
     - cron: "0 3 * * 1"
```

### 5. `.claude/rules/traceability.md`（除外規定・補正規定の追記）

`## 守ること` の後へ以下を追記する:

```markdown
## コミットメッセージの機械チェック（CI・再発防止）

PR で追加されるコミット（`base..HEAD`）の件名を `scripts/check-commit-messages.js` が検査し、規約
`種別(起点ID): 要約` に違反していれば CI を失敗させる（Issue #60）。既存履歴は書き換えず、再発防止のみを目的とする。

- **許可する種別**: `feat` / `fix` / `perf` / `refactor` / `docs` / `test` / `build` / `ci` / `style` / `chore`。
- **起点 ID の書式**: `FR-\d+` / `NFR` / `UC-\d+` / `SC-\d+` / `ADR-\d{3,4}` / `IADR-\d{3,4}` / `P0`〜`P3`。
  複数 ID はカンマ区切りで併記。スコープ `()` は省略可。
- **末尾の PR 番号**: ` (#123)` はスカッシュマージ既定件名として許容。

### 検査対象から除外する自動コミット

- **自動コミットの著者**: `dependabot[bot]` / `renovate[bot]` / `github-actions[bot]` 等の bot 著者。
- **マージコミット**: `--no-merges` により除外。
- **自動生成・リバート**: 件名に `[skip ci]` を含むコミット、および `Revert "..."`。

除外リストは `scripts/check-commit-messages.js` の `BOT_AUTHORS` と同時に更新する。

### 誤記コミットの CHANGELOG 補正（履歴不変更）

過去の誤記コミット（例: `b421761` の件名 `feat(FR-10)` は誤記・実体は P0 骨格）は履歴を書き換えず、
`scripts/changelog-overrides.json` に補正/除外エントリを追加して `scripts/gen-changelog.js` が生成する
`CHANGELOG.md` 上でのみ補正する（Issue #60）。
```

## 未対応・フォローアップ（別 sub issue）

- **openapi.yaml へ FR-08 / FR-10 / FR-11 を手書き追記**: 生成元の通信仕様書（`docs/api/*.md`）が
  存在せず自動生成不可。手書きでのエンドポイント追記が必要（別 sub issue）。
- **CHANGELOG.md の再生成確認**: 本 App 環境は shallow clone（depth 1）＋ネットワーク fetch 制限のため
  全履歴での再生成が不可。`changelog.yml` を develop 起点に変更後、次回 push で自動再生成され、
  `changelog-overrides.json` により `b421761` の FR-10 誤帰属が補正されることを確認する（別 sub issue）。

## 受け入れ基準

- [x] コミット規約チェック `scripts/check-commit-messages.js` が動作し、違反を検出できる（サンプル検証済み）。
- [x] `gen-changelog.js` が `changelog-overrides.json` に基づき `b421761` を FR-10 → P0 に補正する（ユニット検証済み）。
- [ ] 上記「適用が必要な差分」5 件が人手適用され、develop 運用で CI/補助成果物/CodeQL が発火する。
- [ ] openapi.yaml へ FR-08/10/11 が追記される（別 sub issue）。
- [ ] CHANGELOG.md が develop push で再生成され、FR-10 誤帰属が補正される（別 sub issue）。
