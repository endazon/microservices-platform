---
title: planning submodule 最新化と impl-handoff-kit の全面同期
type: spec
status: done
related_ids: [NFR, IADR-0115]
author: Claude
created: 2026-08-01
updated: 2026-08-01
plan_refs: []
---

# 仕様書: planning submodule 最新化と impl-handoff-kit の全面同期

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR: 保守性・運用性。開発基盤の整備）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: [IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)（本作業で新規作成。キットを正とする同期規約）
- 計画書リンク: `planning/tools/impl-handoff-kit/`（`HOWTO.md` / `repo-template/`）

## 目的・背景

計画リポジトリ `project-planning` の submodule pin が `10d8ce2`（ADR-0014）で止まっており、
以降の 9 コミット（`6a1cc9f` まで）を取り込めていない。とくに最新の planning#95
「impl-handoff-kit の Claude 設定・GitHub 設定を是正する」は、**本リポジトリで現に壊れている設定**
（`claude_args` の記法不具合により CI の AI 実装・レビューがツールを使えない）の是正を含む。

同時に、本リポジトリは `impl-handoff-kit/repo-template` から生成された足場を持ちながら、
キット側の改善が長期間反映されていない。以後の乖離を防ぐため、本作業で**キットを正**として
全ファイルを同期し、リポジトリ固有の逸脱を意図的な最小集合へ絞り込む。

## 対象範囲

- 対象: `planning` submodule の pin 更新（`10d8ce2` → `6a1cc9f` → `12cc9b8`）と、
  `impl-handoff-kit/repo-template` 配下の全ファイルの本リポジトリへの反映。
  キットに不足していた点の計画リポジトリへのフィードバック起票（`feedback/`）。
- 対象外: `src/` 配下のアプリケーション実装、`deploy/`、`src/ai-stock-trading` submodule の pin。
  CHANGELOG.md（`changelog.yml` の生成物）。

## 設計

### 方針（IADR-0115）

`repo-template` の各ファイルを 3 分類し、分類ごとに機械的に扱う。

| 分類 | 扱い | 例 |
| --- | --- | --- |
| A. キット完全一致 | キットの内容で上書きする | `.claude/settings.json` / `scripts/check-commit-messages.js` |
| B. キット＋固有デルタ | キットを土台に、固有部分のみ再適用する | `CLAUDE.md`（技術スタック別ルール）/ `ci.yml` |
| C. 本リポの中身そのもの | 変更しない | `docs/adr/README.md` / `docs/operations/operations.md` / `.gitignore` |

分類 B で許容する「固有デルタ」は次の 4 種のみとする。それ以外の独自記述は本作業で削除する。

1. リポジトリ構成（ユニット第一構成 `src/*/{backend,frontend}`・submodule 取得ステップ）
2. 技術スタック（.NET 10 / React+Vite / npm workspaces）とその CI 配線
3. 本リポにしか存在しない成果物・スクリプト（`images.yml` / `check-unit-dependencies.js` 等）
4. Dependabot が更新した **Actions のバージョン**（キットより新しい側を常に採る）

### 反映内容

**A: キットで上書き**

- `.claude/commands/plan-feedback.md` — 環流の主経路を GitHub Issue へ変更
- `.claude/hooks/check-impl.js` — 作業仕様書の有無をブランチ差分で判定（既存蓄積による形骸化を防ぐ）
- `.claude/settings.json` — `Grep`/`Glob`/`Bash(git show:*)`/`gh issue view` 等を allow に追加
- `.github/dependabot.yml` / `.github/workflows/pr-title.yml`
- `scripts/check-commit-messages.js` — ADR/IADR の実在性検査を追加
- `scripts/check-ai-workflow-config.js` — 新規（キットに追加されたもの）
- `AGENTS.md`

**B: キット＋固有デルタ**

- `CLAUDE.md` — キット本文 ＋ 既存の「技術スタック別ルール」節を保持
- `AI_SETUP.md` — キット本文 ＋ プロファイル宣言 `[x] claude-code` を保持
- `.claude/rules/traceability.md` — キット本文 ＋ 本リポの名前空間定義（MSP の ID レンジ、
  `AST` / `planning` の短縮修飾、短縮形へ寄せる決定）のみを固有節として残す
- `docs/ai-workflow.md` — キット本文 ＋ `images.yml` / `image-build` 必須チェックの記述を保持
- `docs/README.md` / `scripts/README.md` — キットの行を取り込み、本リポ固有の行を保持
- `.github/workflows/changelog.yml` — キット本文（`AUTOMATION_PR_TOKEN` フォールバック・既知の制約の注記）
  ＋ Actions は新しい方（`setup-node@v7` / `create-pull-request@v8`）
- `.github/workflows/openapi.yml` — キット本文 ＋ `paths: src/*/backend/**` ＋ 新しい Actions
- `.github/workflows/security.yml` — キット本文（gitleaks 誤検知の運用注記）
  ＋ `src/*` submodule 取得ステップ ＋ `src/*/backend/backend.slnx` の明示ループ ＋ `setup-dotnet@v6`
- `scripts/setup.sh` — キット本文 ＋ `src/*/backend/*.slnx` の明示ループ
  （キット既定の「maxdepth 4 で自動発見」は `templates/unit-template/backend/backend.slnx` を拾い、
  `dotnet restore` が `無効なフレームワーク識別子`（exit 1）で失敗することを実測で確認したため）
- `.github/workflows/doc-links-planning.yml` — キット本文（`timeout-minutes`・失敗時 issue 起票）
  ＋ `setup-node@v7`
- `.github/workflows/claude-coding.yml` / `claude-code-review.yml` — キット本文で全面置換
  （`--allowedTools` を引用符付きカンマ区切りの 1 引数へ是正、`concurrency` / `timeout-minutes`、
  レビュー用プロンプトの計画書探索順・MCP 名の注記）＋ `setup-dotnet@v6`
- `.github/workflows/ci.yml` — キットの `ai-workflow-config` ジョブを追加（他ジョブは現状維持）
- `scripts/scripts.test.js` — キットのテストブロック 2 件
  （`check-ai-workflow-config` / `validateIdExistence`）を復元

**C: 変更しない**

`.gitignore`（キットは真部分集合）・`.gitmodules`・`CHANGELOG.md`・`docs/adr/README.md`・
`docs/operations/operations.md`・`docs/security/security.md`・`docs/tech/tech-requirements.md`・
`.github/workflows/{codeql,frontend,frontend-tests,copilot-setup-steps,images,image-mapping}.yml`・
`scripts/check-doc-links.js`（本リポ側がキットより進んでいる）・`.claude/agents/traceability-auditor.md`

### 計画リポジトリへのフィードバック

キット側の不足として次を `feedback/` に起票し、GitHub Issue 本文を用意する。

1. `check-doc-links.js` の submodule 判定が `planning/` 固定。本リポの
   `.gitmodules` 由来の一般化（MSP #283）をキットへ取り込むべき。
2. キットの GitHub Actions のバージョンが古い（`setup-node@v6` / `setup-dotnet@v5` /
   `create-pull-request@v7` / `upload-artifact@v4`）。キットは Dependabot の対象外のため、
   同期のたびに実装リポ側が再度バンプする必要がある。
3. `.claude/agents/traceability-auditor.md` にキット自身の規約
   （修飾付き ID を突合対象から除外する）が書かれておらず、`traceability.md` と自己不整合。
4. `docs/how-to/`（使い方・デプロイ手順）が仕様書の種別表に無い。
5. `copilot-setup-steps.example.yml` の .NET が `8.0.x` で、`ci.example.yml`（`10.0.x`）と不整合。
6. `setup.sh` / `security.example.yml` のソリューション自動発見（`find . -maxdepth 4`）が、
   ビルド不可の雛形ソリューション（本リポの `templates/unit-template/`）を拾って失敗する。

### 第 2 ラウンド（planning#98 反映後の再同期）

初回同期（pin `6a1cc9f`）で起票した [planning#96](https://github.com/endazon/project-planning/issues/96) の
6 件が planning#98（`12cc9b8`）で**すべてキットへ反映された**（ai-stock-trading からの planning#97 と
併せて計 12 件）。同 pin へ再同期し、固有デルタを次のとおり**縮小**した。

**固有デルタが解消した（キットとバイト一致に戻った）ファイル**

- `scripts/check-doc-links.js` — キットが `.gitmodules` 由来の判定へ一般化（指摘 1）
- `scripts/setup.sh` / `.github/workflows/security.yml` — キットの自動発見が `./templates/*` を
  除外するようになった（指摘 6）。明示ループの固有デルタを撤去した
- `.claude/agents/traceability-auditor.md` — キットが修飾付き ID の除外規則を同梱（指摘 3）
- `.claude/commands/new-spec.md` / `docs/README.md` / `CLAUDE.md` — `runbook` / `how-to` 種別が
  正式化（指摘 4）。`docs/how-to/.gitkeep` を追加
- `scripts/gen-changelog.js` / `scripts/commit-allowlist.json` — テスト注入可能な `applyOverride` と、
  実データ非依存の allowlist テンプレートを取り込み

**この再同期で見つかった本リポジトリ側の欠陥（キットの新テストが検出）**

`scripts/commit-allowlist.json` に載っていた 5 件の SHA は、**本リポジトリの git 履歴に 1 件も
存在しなかった**（`git cat-file -t` が全件失敗＝キットが言う「幻 SHA」）。他リポジトリの
allowlist をそのまま引き継いだものと考えられる。実害としては、規約チェックの除外リストが
**何も除外していないのに『除外実績がある』ように見え**、以後の追加を正当化しかねない状態だった。

`origin/develop` の全履歴（bot / merge / `[skip ci]` を除く）を `validateSubject` で走査したところ
**非準拠コミットは 0 件**であったため、allowlist はキットのテンプレート（空）へ戻した。
以後は `scripts.test.js` の 3 テスト（完全 SHA と reason の存在 / 履歴実在と到達可能性 /
準拠件名を無意味に除外していないこと）が同型の混入を機械的に止める。

**残した固有デルタ**

- `.github/workflows/copilot-setup-steps.yml` — 雛形除外がキット側に未反映のため、
  `src/*/backend/*.slnx` の明示ループを維持する。`.NET` は `8.0.x` → `10.0.x` へ揃えた（指摘 5）。
  キットへは planning#96 のコメントで追報する。
- `.github/workflows/doc-links-planning.yml` — `.example` 由来の「本ファイルをリネームする」手順を
  除去（有効化済みの実ファイルのため。PR #433 の AI レビュー指摘）。
- `.github/workflows/frontend.yml` / `frontend-tests.yml` — キットは IADR 参照を汎用化のため
  削除したが、本リポジトリでは IADR-0033 / IADR-0034 / IADR-0056 が実在するため残す。
- `scripts/verify-qdrant-attribute-payload.sh` — キットからは削除された（MSP 固有のため妥当）。
  本リポジトリの成果物として保持する（IADR-0014 / #71）。

## 受け入れ基準

- [x] `planning` submodule が `origin/main`（`12cc9b8`）を指す
- [x] 分類 A のファイルが `repo-template` と **バイト一致**する
- [x] 分類 B のファイルが、キット由来の記述をすべて含み、固有デルタが上記 4 種に限られる
- [x] `node scripts/check-ai-workflow-config.js --self-test` と実チェックが成功する
- [x] `node scripts/scripts.test.js` が全件成功する
- [x] `node scripts/check-doc-links.js` が成功する
- [x] `node scripts/check-commit-messages.js --title "chore(NFR,IADR-0115): planning submodule を最新化し impl-handoff-kit を全面同期する"` が成功する
- [x] キットへの不足 6 件が `feedback/` に記録され、計画側へ起票する本文が用意されている

## テスト方針

本作業はコードのふるまいを変えないため、既存の機械検査で回帰を確認する。

- `scripts/scripts.test.js`（キット由来のテストブロック復元を含む）
- `scripts/check-ai-workflow-config.js --self-test` ＋ 実ツリー検査
  （＝ワークフローの `--allowedTools` 記法是正が効いていることの検証）
- `scripts/check-doc-links.js`（本仕様書・IADR からの相対リンク）
- 差分検証: 分類 A は `diff` でバイト一致を確認する

## 計画書との差異

- 差異: あり。キット側の不足 6 件（上記「計画リポジトリへのフィードバック」）。
  いずれも本リポジトリ側で先行対応済み、またはキットの内部不整合であり、
  `/plan-feedback` の記録として `feedback/20260801_impl-handoff-kit-gaps.md` に残し、
  計画リポジトリへ [planning#96](https://github.com/endazon/project-planning/issues/96) として起票済み。

## 未決事項

- なし（キット側の不足はフィードバックとして環流し、キットの更新を待って再同期する）
