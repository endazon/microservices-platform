---
title: 作業仕様書 — CI・補助成果物ワークフローの develop 運用整合
type: spec
status: review
related_ids:
  - NFR
author: claude
created: 2026-07-04
updated: 2026-07-04
plan_refs:
  - "../../CLAUDE.md（補助成果物の自動生成 / 自動化・検証・安全 / CI ゲート）"
related_specs:
  - ../../docs/operations/operations.md
  - ../../docs/tech/tech-requirements.md
related_adrs:
  - IADR-0015
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

本 PR を作成した Claude GitHub App は **`.github/workflows/` 配下および `.claude/` 配下を変更できない**。
そのため App 実行時点では、新規スクリプト `scripts/check-commit-messages.js` と本仕様書のみをコミットし、
ワークフロー・ルールの差分は「後述「適用が必要な差分」」として提示するに留めた。

**2026-07-04 追記（適用状況）**: 後述「適用が必要な差分」のうち **ワークフロー 4 件はブランチへ適用済み**
（`git diff origin/develop...HEAD` に反映）。**`.claude/rules/traceability.md` は旧文言のまま適用されており
再適用が必要**、また **PR #76 レビュー 🔴 の CI テスト配線（`ci.yml` へ `scripts.test.js` 実行を追加）は未適用**。

- 適用済み（ワークフロー）: `ci.yml`（develop 追加＋`commit-messages` ジョブ）/ `changelog.yml`（develop 追加）/
  `openapi.yml`（develop 追加＋`--force` 撤去）/ `codeql.yml`（push・pull_request を develop 追加）。
- **未適用（要人手・下記「適用が必要な差分」参照）**:
  1. `ci.yml` に単体テスト実行（`node scripts/scripts.test.js`）ジョブ／ステップを追加（PR #76 レビュー 🔴）。
     現状 `scripts/scripts.test.js`（19 ケース）はどの CI ジョブからも実行されておらず、回帰防止として機能していない。
  2. `.claude/rules/traceability.md` を現行仕様へ再適用（スコープ必須化・`P0`〜`P3` 追加・`commit-allowlist.json`
     除外の追記。旧文言「スコープ `()` は省略可」は `check-commit-messages.js` の実装と矛盾するため要修正）。
  3. `openapi.yml` の雛形フォールバック末尾の `|| true` は不要（`gen-openapi-skeleton.js` は既存ファイルがあっても
     exit 0 で正常終了するため。害は無いが起こり得ないケースへの防御。PR #76 レビュー 🟢）。
- `scripts/check-commit-messages.js` は `origin/develop..HEAD` に対して実行し、規約適合を確認済み（EXIT=0）。
- `scripts/scripts.test.js` はローカルで **19 ケース pass** を確認済み（`node scripts/scripts.test.js`）。

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

- openapi.yaml の「再生成」は機械実行しない。FR-08 / FR-10 / FR-11 の未反映 API は**手書き仕様への追記**で対応する（本 PR のスコープ外・別 PR 推奨）。
- 併せて `openapi.yml` の雛形フォールバック（`--force`）が手書き仕様を破壊しないようガードすべき（下記差分・要検討）。

## 対応方針の確定

### ブランチ運用（方針: (a) develop をトリガーへ追加）

CLAUDE.md は「`main` を安定版とする」とするが、実運用の既定ブランチは `develop`。二重管理を避けるため、
本作業では **(a) 各ワークフローのトリガーへ `develop` を追加**する方針を採る（(b) 定期リリースマージ運用は
別途 `operations.md` で確立する将来課題）。補助成果物の自動コミット先も `develop` になる点に留意する。

## 適用が必要な差分（`.github/workflows/` は人手で適用）

### 1. `ci.yml`（push を develop 起点に）

```diff
 on:
   push:
-    branches: [main]
+    branches: [develop, main]
   pull_request:
     types: [opened, synchronize, reopened]
+
+jobs:
+  # コミットメッセージ規約（種別(起点ID): 要約）の機械チェック（Issue #60・再発防止）
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
```

> 注: 上記 `jobs:` 断片は既存 `jobs:` 直下へ 1 ジョブとして追加する（`doc-links` 等と並列）。
> `fetch-depth: 0` は `base..HEAD` の範囲解決に必須。

さらに、`scripts/scripts.test.js`（`validateSubject`/`findAllowlisted`/`applyOverride` の回帰 19 ケース）を CI で
実行し、既知の抜け穴が再発しても検知できるよう配線する（PR #76 レビュー 🔴）。`doc-links` と同様に
Node 標準モジュールのみで動くため依存インストール不要。既存 `jobs:` 直下へ以下を追加する:

```diff
+  # スクリプト単体テスト（check-commit-messages / gen-changelog の回帰防止・Issue #60 レビュー反映）。
+  # Node 標準の assert のみで動作し依存インストール不要。
+  script-tests:
+    runs-on: ubuntu-latest
+    steps:
+      - uses: actions/checkout@v7
+      - uses: actions/setup-node@v6
+        with:
+          node-version: "20"
+      - name: Run script unit tests
+        run: node scripts/scripts.test.js
```

### 2. `changelog.yml`（develop で CHANGELOG を再生成）

```diff
 on:
   push:
-    branches: [main]
+    branches: [develop, main]
     tags: ["v*"]
```

### 3. `openapi.yml`（develop 追加＋破壊防止ガード）

```diff
 on:
   push:
-    branches: [main]
+    branches: [develop, main]
     paths:
       - "docs/api/**"
       - "scripts/generate-openapi.sh"
       - "src/**"
   workflow_dispatch: {}
```

さらに、手書き openapi.yaml を破壊しないよう「生成コマンドが無い場合は雛形生成をスキップする」ガードを推奨:

```diff
           else
-            echo "生成コマンド未設定。通信仕様書から雛形を生成する。"
-            node scripts/gen-openapi-skeleton.js --src docs/api --out docs/api/openapi.yaml --force
+            echo "生成コマンド未設定かつ手書き仕様を尊重。雛形上書きはスキップする。"
+            # 手書き openapi.yaml を破壊しないため --force を付けない（既存があれば上書きしない）。
+            node scripts/gen-openapi-skeleton.js --src docs/api --out docs/api/openapi.yaml || true
           fi
```

### 4. `codeql.yml`（develop 向け PR / push で発火）

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

### 5. `.claude/rules/traceability.md`（現行仕様への整合・要人手再適用）

`.claude/` は App 権限で編集不可のため、以下を人手で適用する。**旧版は「スコープ `()` は省略可」で
`P0`〜`P3`・allowlist の記載が無く、`check-commit-messages.js` の実装と矛盾する**ため、下記のとおり修正する
（PR #76 レビュー 🟡 反映）。

「## 起点 ID の種別」一覧の末尾へ `P0`〜`P3` を追加:

```diff
 - `IADR-xxxx`: 実装ADR（本リポ `docs/adr/`。計画ADR とは別系統）
+- `P0`〜`P3`: フェーズ骨格（基盤スケルトン等、単一 FR/UC に紐づかない横断的なフェーズ作業）
```

「## コミットメッセージの機械チェック」節を以下の内容にする（スコープ必須化・allowlist 除外を反映）:

```markdown
## コミットメッセージの機械チェック（CI・再発防止）

PR で追加されるコミット（`base..HEAD`）の件名を `scripts/check-commit-messages.js` が検査し、規約
`種別(起点ID): 要約` に違反していれば CI を失敗させる（Issue #60）。既存履歴は書き換えず、再発防止のみを目的とする。

- **許可する種別**: `feat` / `fix` / `perf` / `refactor` / `docs` / `test` / `build` / `ci` / `style` / `chore`。
- **起点 ID の書式**: `FR-\d+` / `NFR` / `UC-\d+` / `SC-\d+` / `ADR-\d{3,4}` / `IADR-\d{3,4}` / `P0`〜`P3`。
  複数 ID はカンマ区切りで併記。
- **スコープ（起点 ID）の要否**: 内容変更を伴う種別（`feat` / `fix` / `perf` / `refactor` / `docs` / `test`）は
  スコープ `(起点ID)` を**必須**とする。計画 ID に紐づかない雑多・ツールチェーン変更は `chore` / `style` /
  `build` / `ci` を用い、スコープ省略を許す（`check-commit-messages.js` の `TYPES_ALLOW_NO_SCOPE`）。
- **末尾の PR 番号**: ` (#123)` はスカッシュマージ既定件名として許容。

### 検査対象から除外する自動コミット

- **自動コミットの著者**: `dependabot[bot]` / `renovate[bot]` / `github-actions[bot]` 等の bot 著者。
- **マージコミット**: `--no-merges` により除外。
- **自動生成・リバート**: 件名に `[skip ci]` を含むコミット、および `Revert "..."`。
- **規約導入前の既存コミット（grandfather）**: `scripts/commit-allowlist.json` に完全 SHA と理由を列挙した
  コミットは `skip(allowlist)` として除外する（履歴改変・force push をせずに導入前の非準拠コミットを恒久除外
  するため。将来の新規コミットは通常どおり検査対象）。

除外リストは `scripts/check-commit-messages.js` の `BOT_AUTHORS` および `scripts/commit-allowlist.json` と同時に更新する。
```

## 実装物（本 PR）

### `scripts/check-commit-messages.js`（新規）

- **範囲**: `--range` → `COMMIT_RANGE` → `origin/$GITHUB_BASE_REF..HEAD`（PR）→ `origin/develop..HEAD` の順で決定。既存履歴は検査しない。
- **検査**: 件名を `種別(起点ID): 要約` で検証。種別集合は `gen-changelog.js` と一致。起点 ID 書式・複数 ID 併記・末尾 `(#\d+)` を許容。
- **起点 ID の必須化（PR #76 レビュー 🔴 反映）**: 内容変更の種別（`feat`/`fix`/`perf`/`refactor`/`docs`/`test`）は起点 ID（スコープ）を**必須**とし、無い場合を違反として検出する。計画 ID に紐づかない雑多・ツールチェーン変更は `chore`/`style`/`build`/`ci`（`TYPES_ALLOW_NO_SCOPE`）で表現し ID 省略を許す。これにより「`feat: 説明`（ID 無し）が素通りする」抜け穴を塞ぐ。
- **除外**: bot 著者（`BOT_AUTHORS`）・マージコミット（`--no-merges`）・`[skip ci]`・`Revert "..."`・恒久適用除外リスト（`commit-allowlist.json`）。
- **終了コード**: 違反あり `1`（CI 失敗）／範囲解決不能（浅いクローン等）は `0`（ブロックしない）。
- 外部依存ゼロ（Node 標準モジュールのみ・既存スクリプトの流儀に準拠）。`validateSubject` は `scripts/scripts.test.js` で単体テスト済み。

### `scripts/commit-allowlist.json`（新規・規約導入前コミットの恒久適用除外）

- **目的（PR #76 CI 対応）**: 起点 ID 必須化により、本ブランチに含まれる**規約導入前**の既存コミット
  （`d1652dc`/`394fa1f`/`079490d`/`153810a`/`d4835097`。いずれも起点 ID 無し）が `commit-messages` で失敗する。
  force push 禁止方針のため件名を書き換えられず、必須チェックにすると「落ちるからマージできない／squash マージでしか解消できない」循環が生じる。
- **方針**: `changelog-overrides.json` と同型に、**完全 SHA と理由を明記した恒久適用除外リスト**を設ける。
  `check-commit-messages.js` は一致コミットを `skip(allowlist)`（CI ログに理由付きで常時表示＝監査可能）として検査対象から外す。
  **将来の新規コミットは通常どおり検査対象**（`.git-blame-ignore-revs` と同種の、遡及不能な既存履歴への明示的除外であり抜け穴ではない）。
- **運用ルール**: 本ファイルへ新規コミットの規約違反を安易に追加しない。`findAllowlisted`/`loadAllowlist` は `scripts/scripts.test.js` で単体テスト済み（5 コミットの除外回帰を含む）。

### `scripts/changelog-overrides.json` / `gen-changelog.js`（誤帰属補正）

- **補正方針**: git 履歴は書き換えず、CHANGELOG 生成時のみ誤記コミットを補正/除外する（`action: remap|exclude`）。
- **`b421761` の補正（PR #76 レビュー 🔴 反映）**: 元件名 `feat(FR-10)` は誤記だが、当該コミットは約 9,200 行の P0 基盤スケルトン実装である。したがって `type` は実体どおり **`feat` のまま**、`scope` のみ `FR-10 → P0` に補正する（`docs` へ remap すると大規模実装をドキュメントとして過小計上する新たな誤帰属を生むため不可）。
- **不正 `action` の検出（PR #76 レビュー 🟡 反映）**: `applyOverride` は未知の `action`（タイプミス等）を黙って remap 扱いにせず、警告を出して補正を無視する。
- `applyOverride` / `hashMatches` は `scripts/scripts.test.js` で単体テスト済み（`b421761 → feat/P0` の回帰を含む）。

## 受け入れ基準

- [x] `ci` / `changelog` / `openapi` / `codeql` の各ワークフローが `develop` の push / PR で発火する（差分適用済み）。
- [x] CodeQL が develop 向け PR で解析を実行する（`pull_request.branches: [develop, main]` 適用済み）。
- [x] コミット規約の機械チェックスクリプトが存在し、規約違反コミットを検出して非ゼロ終了する。
- [x] dependabot 等の自動コミット・マージ・`[skip ci]` を検査対象から除外する。
- [x] CHANGELOG / openapi.yaml の再生成方針を明記した（CHANGELOG は `changelog.yml` の develop 発火で自動再生成、
      `feat(FR-10)` 誤記コミット `b421761` は `changelog-overrides.json` により `feat`／scope `P0` へ補正）。
- [x] `check-commit-messages.js`（`validateSubject`/`findAllowlisted`）と `gen-changelog.js`（`applyOverride`）に単体テストを追加した（`scripts/scripts.test.js`・19 ケース pass）。
- [ ] 上記単体テストを CI で実行する（`ci.yml` へ `script-tests` ジョブ追加）。**要人手**（App は `.github/workflows/` を編集不可）。PR #76 レビュー 🔴。
- [ ] `.claude/rules/traceability.md` を現行仕様（スコープ必須化・`P0`〜`P3`・allowlist 除外）へ再適用する。**要人手**（App は `.claude/` を編集不可）。PR #76 レビュー 🟡。
- [x] 規約導入前の非準拠コミット 5 件を `commit-allowlist.json` で恒久適用除外し、`commit-messages` ジョブが `origin/develop..HEAD` で pass する（EXIT=0）ことをローカル検証した。
- [x] 重要な実装判断を実装 ADR（`IADR-0015`）に記録した。
- [x] 本作業仕様書を作成した。

## 残課題・フォローアップ

- **CHANGELOG.md の再生成**: `changelog.yml` を develop で発火させれば `gen-changelog.js` が全履歴から再生成する
  （本 App 環境は shallow clone で全履歴を取得できないため、ここでの手動再生成は行わない）。
  コミット `b421761`（件名 `feat(FR-10)` は誤記・実体は P0 骨格）は `changelog-overrides.json` の remap により
  `feat`／scope `P0` として計上され、FR-10 誤帰属は解消される（履歴は書き換えない）。
- **openapi.yaml への FR-08/10/11 反映**: 手書き 3.1.0 仕様への追記が必要（別 PR 推奨）。
- **CI への単体テスト配線（PR #76 レビュー 🔴・要人手）**: `scripts/scripts.test.js`（19 ケース）はローカルでは
  pass するが、どの CI ジョブからも実行されていないため回帰防止として機能していない。上記「1. `ci.yml`」の
  `script-tests` ジョブ差分を人手で適用する（App は `.github/workflows/` を編集不可）。
- **`.claude/rules/traceability.md` の再適用（PR #76 レビュー 🟡・要人手）**: 旧文言（スコープ省略可・`P0`〜`P3` 欠落・
  allowlist 未記載）が実装と矛盾しているため、上記「5.」の差分を人手で再適用する（App は `.claude/` を編集不可）。
- **`openapi.yml` の不要な `|| true`（PR #76 レビュー 🟢・任意）**: `gen-openapi-skeleton.js` は既存ファイルがあっても
  exit 0 で終了するため `|| true` は不要。害は無いため必須ではないが、整理する場合は上記「3.」参照。
