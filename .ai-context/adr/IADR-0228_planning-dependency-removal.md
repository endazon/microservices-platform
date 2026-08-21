---
title: IADR-0228 本リポジトリは planning submodule に依存しない。関連する検査器・ワークフロー・環流機構を撤去する
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0058
  - IADR-0115
  - IADR-0170
  - IADR-0184
  - IADR-0185
  - IADR-0187
  - IADR-0192
  - IADR-0193
  - IADR-0198
  - IADR-0201
  - IADR-0202
  - IADR-0203
  - IADR-0204
author: claude
created: 2026-08-21
updated: 2026-08-21
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0048_impl-docs-restructure.md
---

# IADR-0228: planning submodule 依存の全面撤去（ADR-0048 決定 2 の実装）

- 状態: Accepted
- 日付: 2026-08-21
- 決定者: 計画側の裁定（計画 `ADR-0048` 決定 2）＋ claude（実装）

## 起点・関連

- 計画 `ADR-0048`「実装ドキュメント構成の再編（`.ai-context/` 移設・planning 依存の全面撤去）」決定 2。
- 本 IADR は、決定 2 が指す「関連する検査器・ワークフロー・環流機構の撤去」を本リポジトリで
  実施した記録である。**個々の撤去対象を定めた旧 IADR（下記）は書き換えず、本 IADR から
  一括で参照する**——15 本前後の frozen ADR を 1 件ずつ書き換えるより、撤去という 1 つの決定を
  1 箇所に記録するほうが `IADR-0141`（母集合の分割・[[IADR-0141]]）の精神に合う。

## コンテキストと課題

`project-planning`（計画リポジトリ）を git submodule `planning/` として参照する構成は、
`.ai-context/` への文書再編（ADR-0048 決定 1）と対を成す形で、計画側の裁定 ADR-0048 決定 2 に
より**全面撤去**の対象になった。submodule 依存を撤去すると、それに付随して構築された
複数の機構（検査器・CI ワークフロー・環流フロー）が同時に意味を失う。

## 決定

**計画 ADR-0048 決定 2 のとおり、planning submodule 依存を全面撤去する。**

1. **submodule 自体の撤去**: `git rm --cached planning` ＋ `.gitmodules` から
   `[submodule "planning"]` 節を削除（`src/ai-stock-trading` は維持）。
2. **撤去した検査器**（4 本。いずれも `scripts/` から削除し、`scripts.repo.test.js` の対応テストも削除）:
   - `check-planning-pin-freshness.js`（設計は [IADR-0170](./IADR-0170_planning-pin-freshness-detection.md) /
     [IADR-0202](./IADR-0202_pin-freshness-comparison-source.md)）
   - `check-kit-sync.js` ＋ `kit-sync-classification.json`（設計は
     [IADR-0192](./IADR-0192_kit-sync-classification-and-check.md) /
     [IADR-0198](./IADR-0198_kit-delta-fifth-kind-and-review-verdict.md) /
     [IADR-0201](./IADR-0201_class-c-rejudgement-and-fail-closed-kit-checks.md) /
     [IADR-0204](./IADR-0204_kit-catchup-deferral-with-expiry-ratchet.md)）
   - `check-feedback-dispatched.js`（設計は [IADR-0184](./IADR-0184_feedback-dispatch-checker-verbatim.md)）
   - `check-feedback-status-sync.js`（設計は [IADR-0185](./IADR-0185_feedback-status-vocabulary.md) /
     [IADR-0187](./IADR-0187_status-vocabulary-follows-upstream-adjudication.md) /
     [IADR-0193](./IADR-0193_feedback-status-sync-check.md)）
3. **撤去した CI ワークフロー**: `.github/workflows/doc-links-planning.yml`（設計は
   [IADR-0058](./IADR-0058_doc-links-planning-submodule-ci.md)）、
   `.github/workflows/planning-pin-freshness.yml`。`ci.yml` から `feedback-dispatched` /
   `kit-sync` / `feedback-status-sync` ジョブと `PLANNING_REPO_TOKEN` を使う submodule fetch を削除。
   `claude-code-review.yml` / `claude-coding.yml` から planning submodule の fetch ステップと
   `git -C planning` の allowedTools エントリを削除。
4. **撤去した環流機構**: 本リポジトリの `feedback/`（50 記録 + README + TEMPLATE）を削除。
   計画リポジトリ側で全件の写しが存在すること（`projects/microservices-platform/10_feedback/`）を
   削除前に確認済み。以後の環流は計画リポジトリの GitHub issue（`decision-needed` ラベル）に
   一本化する（計画 ADR-0048 決定 5）。
5. **`.github/dependabot.yml`**: [IADR-0203](./IADR-0203_renovate-husky-hook-scope.md) 決定 1・5 が
   「編集しない」「`gitsubmodule` は `planning-git` レジストリで維持する」と定めていたが、
   **本決定により上書きする**（詳細は同 IADR への 2026-08-21 追記）。`registries: planning-git` 節と
   `gitsubmodule` 更新エントリの `planning-git` 参照を削除した。`src/ai-stock-trading` の
   `gitsubmodule` エントリは維持する。
6. **`.claude/settings.json`**: `Bash(git -C planning ...)` の permission エントリ（5 件）を削除
   （`src/ai-stock-trading` 系のエントリは維持）。

## 理由

- **計画 ADR が実装 ADR に優先する**——本リポジトリの禁止事項（`CLAUDE.md`）が明記するとおり、
  計画書に反する実装は許されず、逆に計画側の裁定は実装側の既存決定を上書きしてよい。
- **撤去対象の機構は前提（submodule の populate）を失っており、動作しない状態で残すほうが害である**
  ——`check-planning-pin-freshness.js` 等は submodule 不在では常に fail-open か fail するだけで、
  検査としての意味を持たない。
- **旧 IADR を書き換えないのは記録保全のため**——各 IADR は「その時点でなぜその設計にしたか」の
  記録として妥当であり続ける。撤去という新しい決定は新しい IADR に置き、個々の旧 IADR には
  直接影響する箇所（[IADR-0203](./IADR-0203_renovate-husky-hook-scope.md) の「これを覆さない」のように
  明示的に将来を拘束する文言があるもの）にだけ、日付つき追記で本 IADR への導線を残す。

## 結果

- `git submodule status` に `planning` は現れない（`src/ai-stock-trading` のみ）。
- `scripts/` の検査器総数は 37 → 34（本 IADR の 4 本撤去に加え、trace ブロック検査
  `check-trace-blocks.js` 1 本の新設が同一 PR に含まれる。37 − 4 + 1 = 34）。
  `scripts.test.js` の母集合ラチェットが追随する。
- `check-commit-messages.js` の計画 ADR 実在集合は、ファイル走査（旧 submodule の
  `projects/<name>/07_adr/`）から **`.claude/rules/traceability.repo.md` の宣言レンジ**（`check-trace-blocks.js`
  の `planAdrRange()` を再利用）へ切り替えた。submodule を populate しない CI でも計画 ADR 検査が
  実効する（旧規範「CI は計画 ADR の実在性を守っていない」は解消。経緯は
  `docs/how-to/plan-id-range-history-annex.md` §3 の 2026-08-21 追記）。
- `docs/adr/` → `.ai-context/adr/`、`docs/specs/` → `.ai-context/specs/` の移設（ADR-0048 決定 1）と
  合わせて、`CLAUDE.md` / `AGENTS.md` / `AI_SETUP.md` / `README.md` / `src/README.md` /
  `deploy/local/**/README.md` / `templates/unit-template/README.md` 等の相対リンクを実在パスへ
  追随させた（本 IADR と同じコミット群）。
- フォローアップ: `docs/how-to/adr-supersede-citation-annex.md` §1（機械検査を置いていない理由の
  測定）も、根拠にしていた「PR 文脈で planning submodule を populate する 2 本の例外ワークフロー」
  が撤去されたため、2026-08-21 追記で反映済み。

## 関連

- Supersedes: なし（個々の旧 IADR は Superseded にはしない。上記「理由」参照）
- Superseded by: なし
