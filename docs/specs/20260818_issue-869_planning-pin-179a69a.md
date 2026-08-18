---
title: 作業仕様書 — 計画 pin を 179a69a へ進め、キット大幅更新（役割スロット制・必読の別紙化・検査強化）へ追随する
type: spec
status: done
related_ids:
  - NFR
  - IADR-0192
  - IADR-0198
  - IADR-0204
  - IADR-0223
author: claude
created: 2026-08-18
updated: 2026-08-18
plan_refs:
  - "../../planning/tools/impl-handoff-kit/HOWTO.md (§B-5 差し替え表・検査器の実走突合)"
  - "../../planning/tools/impl-handoff-kit/repo-template/ (pin 179a69a)"
  - "../../planning/docs/ai-implementation-workflow-guide.md (§8 予算 / §11 パリティ)"
related_specs:
  - "20260818_planning-pin-282c2d0.md"
---

# 作業仕様書: 計画 pin `179a69a` の追随（issue #869）

## 1. 起点となる ID（トレーサビリティ）

- **無採番 `NFR`**（キット追随・pin 更新＝メタ作業。`.claude/rules/traceability.md`「無採番 `NFR` を許す 2 つの場合」の**場合 2**）。
- 関連: `IADR-0192` / `IADR-0204`（キット追随の分類と保留のラチェット）/ `IADR-0198`（分類 5 の定義）/ `IADR-0223`（予算は上限と下限の両方）。
- 起点 issue: #869。

## 2. 母集合の引き方（実測）

**走査基準**: `claude/plan-to-impl-repo-sync-dfra4r` `16be659`（origin/develop 先端に一致）。**pin**: `282c2d0` → `179a69a`（**4 コミット**）。

```text
git -C planning log --oneline 282c2d0..179a69a
  179a69a fix(kit): 配布物の裸 issue 番号を名前空間ごとに修飾する（planning#418） (planning#419)
  3ee79af fix(kit): 件名の HTML エンティティを検査し、grep を含む 5 サブコマンドへ揃える（planning#414 planning#415） (planning#417)
  9ced1df docs: Claude 関連ツールのトークン効率監査と対応（cookbooks 準拠・配布必読 43→32KB） (planning#416)
  00daaf0 docs(NFR): 実装キットを役割スロット制にし、司令塔・作業 AI を差し替え可能にする (planning#413)
```

**追随対象の母集合は記憶で挙げず、2 系統の機械出力から取った**（母集合の規則 9）。

1. `KIT_DIR=<planning 179a69a> node scripts/check-kit-sync.js --require-planning` の実走出力: **違反 21 件**（未分類 6 / 分類 A ドリフト 15）。
2. `git -C planning diff --name-status 282c2d0..179a69a -- tools/impl-handoff-kit/repo-template/`: **33 ファイル**（新規 6 / 変更 27）。

1 は分類 B の土台変更と notApplicable（実名化済みワークフロー）を構造的に上げないため、2 と突合して全 33 件を §3 の表へ割り当てた（黙った除外ゼロ）。

**除外したもの**: `planning` 側の `tools/impl-handoff-kit/HOWTO.md`・`generators/*`・`ai-context/README.md`（キットの手引き・計画側実行物であり配布物ではない）、`draft/feedback/*`・`docs/ai-implementation-workflow-guide.md`（計画リポの記録・正本であり本リポが複写する対象ではない）。

## 3. 対象範囲（キット側 33 ファイルの全数割り当て）

| # | キット側ファイル | 分類 | 扱い |
| --- | --- | --- | --- |
| 1 | `.claude/agents/adr-guardian.md` | A | キット原文で上書き |
| 2 | `.claude/agents/spec-implementer.md` | B（X → **3** に再判定） | 土台変更（作業開始前の改番・自動読込の注記）を移植。表＝本リポ固有規範は保持（キットは planning#337 で「表を写さない」が確定＝恒久デルタ） |
| 3 | `.claude/agents/traceability-auditor.md` | A | キット原文で上書き |
| 4 | `.claude/commands/impl-feature.md` | A | キット原文で上書き |
| 5 | `.claude/rules/traceability.md` | A | キット原文で上書き（🔴 別紙 #17 と同時） |
| 6 | `.claude/settings.json` | B 1 | **利用者適用**（Edit/Write とも deny。§7 未了へ） |
| 7 | `.github/workflows/ci.example.yml` | 対象外 | 実名 `ci.yml` へ差分を移植 |
| 8 | `.github/workflows/claude-code-review.example.yml` | 対象外 | 実名 `claude-code-review.yml` へ差分を移植 |
| 9 | `.github/workflows/claude-coding.example.yml` | 対象外 | 実名 `claude-coding.yml` へ差分を移植 |
| 10 | `AGENTS.md` | B 3 | 土台変更を移植（固有 1 行は保持） |
| 11 | `AI_SETUP.md` | B 5+1+2 | 土台変更を移植 |
| 12 | `CLAUDE.md` | B 2+1 | 土台変更を判定して移植（予算内・§5） |
| 13 | `ai-roster.json` | **新規 → B 5** | 取り込み（キット雛形と同じ分類。配役は本リポが埋める欄） |
| 14 | `docs/ai-orchestration.md` | **新規 → A** | 取り込み（キット雛形と同じ分類） |
| 15 | `docs/ai-workflow.md` | B 2 | 土台変更を移植 |
| 16 | `docs/traceability-appendix.md` | A | キット原文で上書き（#5 と同時） |
| 17 | `scripts/README.md` | B 3 | 土台変更を移植 |
| 18 | `scripts/action-versions.json` | A | キット原文で上書き |
| 19 | `scripts/ai-adapters/README.md` | **新規 → A** | 取り込み |
| 20 | `scripts/ai-adapters/run-worker-claude.sh` | **新規 → A** | 取り込み |
| 21 | `scripts/ai-adapters/run-worker-codex.sh` | **新規 → A** | 取り込み |
| 22 | `scripts/ai-adapters/run-worker.sh` | **新規 → A** | 取り込み |
| 23 | `scripts/apply-profile.sh` | A | 実走突合のうえキット原文で上書き |
| 24 | `scripts/check-action-versions.js` | A | 実走突合のうえキット原文で上書き |
| 25 | `scripts/check-ai-workflow-config.js` | A | 実走突合のうえキット原文で上書き |
| 26 | `scripts/check-commit-messages.js` | B 5 | 土台変更（HTML エンティティ検査）を移植。置換点 `PLAN_PROJECT` は保持 |
| 27 | `scripts/check-doc-links.js` | B（X → **A** に再判定） | キット側にベア名検査（#609 環流）が着地し、残差が自己試験ラベルの文言のみ＝固有デルタ 0 と実測（機能差 diff で確認）。キット原文で上書き |
| 28 | `scripts/check-kit-sync.js` | A | 実走突合のうえキット原文で上書き |
| 29 | `scripts/check-permission-denials.js` | A | 実走突合のうえキット原文で上書き |
| 30 | `scripts/check-review-verdict.js` | A | 実走突合のうえキット原文で上書き |
| 31 | `scripts/kit-sync-classification.example.json` | A | キット原文で上書き |
| 32 | `scripts/lib/ci-annotate.js` | A | 実走突合（scripts.test.js 経由）のうえキット原文で上書き |
| 33 | `scripts/scripts.test.js` | A | キット原文で上書き（`scripts.repo.test.js` は別ファイルのため無傷） |

**波及先（キット diff の外）**: `planning` gitlink（`282c2d0` → `179a69a`）／`scripts/kit-sync-classification.json`（新規 6 件の分類・`$comment` の pin 記述）／本仕様書。

## 4. 設計（進め方）

1. pin 前進 → `check-kit-sync.js` 実走（母集合の確定。§2）
2. 分類 A の検査器は、上書き前に**新旧 CLI を同一入力で実走突合**する（HOWTO の原則。裁定 planning#343）。差があればキット版を採らず B へ移して環流する
3. 分類 B は `git -C planning diff 282c2d0..179a69a -- <対象>` を読み、**固有デルタを保持したまま土台だけを追随**する
4. 実名ワークフロー 3 本は example 差分の**行単位突合**で移植する（notApplicable のため機械検査が届かない。**Actions のバージョンは高い方を残す**）
5. 分類表・仕様書を更新し、§6 の検証を全部実走する

## 5. 受け入れ基準

- [x] `node scripts/check-kit-sync.js --require-planning` が exit 0（未分類 0・ドリフト 0）
- [x] §3 の 33 件すべてが「上書き／移植／対象外（移植先を明記）／利用者適用」のいずれかで処理済み
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が全件 pass（666 件）
- [x] `check-reading-budget.js` が予算内（42,171B / 51,200B・82.4%）
- [x] その他ローカル検査（`check-adr-numbering` / `check-doc-links` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-doc-type-vocabulary` / `check-feedback-dispatched` / `check-action-versions --compare-with-ref origin/develop` / `check-ai-workflow-config`）が全て exit 0

## 6. 検証（実測）

`planning` を pin `179a69a` で populate した状態で実測した。

```text
node scripts/check-kit-sync.js --require-planning
  OK: キット 123 件（A 86 / B 25 / C 4 / 対象外 8）                                   exit=0
node scripts/check-adr-numbering.js / check-doc-links.js / check-cross-repo-refs.js /
  check-plan-id-qualification.js / check-doc-type-vocabulary.js /
  check-feedback-dispatched.js / check-feedback-status-sync.js /
  check-ai-workflow-config.js                                                        全て exit=0
node scripts/check-action-versions.js --compare-with-ref origin/develop              exit=0
node scripts/check-reading-budget.js
  Claude Code: 42,171B / 51,200B（82.4%）……別紙化（planning#416）で 49,902B から純減   exit=0
REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js   ✓ 666 tests passed
bash -n scripts/apply-profile.sh scripts/ai-adapters/*.sh                            ok
python3 -c "yaml.safe_load(...)"  claude-coding / claude-code-review / ci            ok
```

**検査器の実走突合**（HOWTO の原則・裁定 planning#343）: 分類 A の検査器は「旧 pin のキット原文＝現物」であり、キット HEAD 版は同一配布物の前進である（X/B の差し替えと異なり、実装側の独自改善が消える経路が無い）。そのうえで `check-kit-sync` / `check-action-versions` / `check-ai-workflow-config` は差し替え前後で同一入力の exit code を突合し（同値）、差し替え後に全 CLI と `--self-test` を実走した。`check-doc-links.js`（X → A）は機能差 diff（コメント・自己試験ラベル以外の差分ゼロ）と差し替え後の本走・自己試験 39 件で確認した。

**固有テストの追随 1 件**: キットの別紙化で見出し「検査対象から除外する自動コミット」が配布物から消え、`scripts.repo.test.js` #686 段 1（確定済み記録の引用が指す先の保持）が fail した。companion `.claude/rules/traceability.repo.md` へスタブ見出しを追加して解消（配布物は編集しない）。

**CI 初回実走での検出 1 件（是正済み）**: 取り込んだ HTML エンティティ検査（planning#417）が、検査導入前に develop へ着地済みの件名 `e3cb107`（`&lt;` を含む）を「baseline 外の違反」として検出し、`scripts-tests` が赤になった（本ローカルは当時 shallow clone のため skip されており、`git fetch --unshallow` 後に再現を確認した）。検査器の案内どおり `landed-subject-baseline.json` へ追記し、`changelog-overrides.json` の `remap` で生成物側を是正した（履歴は不変）。

## 7. 計画書との差異・未決事項

- **`.claude/settings.json`（キット側の変更はコメント 1 行＝ 4→5 サブコマンド化と issue 参照の修飾）は本セッションから編集できない（Edit/Write とも deny）。** 本リポの実物は #856 で先行して 5 サブコマンド化・修飾済みであり、**実質差分は無い**。なおコメント末尾の【暫定デルタ】節（「キット側の是正を環流したら本デルタは撤去する」）は、planning#419 の着地により**撤去可能になった**。利用者の適用に委ねる（#847 から継続の扱い）。
- 実名ワークフロー 3 本への移植内容: `claude-coding.yml`＝位置づけヘッダ・既定モデルを `claude-sonnet-5` へ（裁定 2026-08-18）・モデル注記。`claude-code-review.yml`＝位置づけヘッダ・実行制約の凝縮（バックグラウンド待ち禁止と変更範囲絞りを含む——**旧文面はこの 2 弾を持っておらず、example 側の先行改善が届いていなかった**）・検証の誠実性の導入 3 行を YAML コメントへ移設・【プロンプトの書き方】コメント新設・`ls-tree / grep` の 2 箇所追随。`ci.yml`＝キット側変更（issue 修飾 1 行）は #866 で先行済みのため差分なし。
- Actions のバージョン: キットの下限表更新は参照修飾のみで、版の巻き戻り無し（`--compare-with-ref origin/develop` で機械確認）。
