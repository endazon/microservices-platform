---
title: 作業仕様書 — 計画 pin を 4d6a7d6 へ進め、母集合定義・分類 C 再判定・§11 パリティ・Windows パス不整合を追随する（#755。#751 を束ねる）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0115
  - IADR-0139
  - IADR-0172
  - IADR-0190
  - IADR-0192
  - IADR-0193
  - IADR-0200
  - IADR-0201
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - planning:docs/ai-implementation-workflow-guide.md
  - planning:tools/impl-handoff-kit/repo-template/scripts/kit-sync-classification.example.json
  - planning:tools/impl-handoff-kit/repo-template/CLAUDE.md
  - planning:draft/feedback/20260815_kit-class-c-definition-ambiguous.md
  - planning:draft/feedback/20260815_reading-budget-mother-set-undefined.md
related_specs:
  - "../adr/IADR-0200_reading-budget-population-per-agent.md"
  - "../adr/IADR-0201_class-c-rejudgement-and-fail-closed-kit-checks.md"
  - "../../docs/how-to/plan-id-range-history-annex.md"
  - "20260815_planning-pin-ce9abd2.md"
---

# 作業仕様書: 計画 pin を `4d6a7d6` へ進め、planning#363 / planning#364 の裁定と運用ガイド §8・§11 を追随する

## 1. 起点となる ID（トレーサビリティ）

- 起点 ID: **NFR**（計画の追随・規約・検査器＝メタ作業。当たる `NFR-xx` が無いため無採番。[IADR-0179](../adr/IADR-0179_unnumbered-nfr-for-meta-work.md)）
- 起点 issue: **#755**（パリティ棚卸し 2026-08-16。**AST#524 と対**＝同時起票・7 日後突合の対象）。**#751 を束ねた**（§7）
- pin: `b640159` → **`4d6a7d6`**（1 コミット。PR planning#365）
- 分類（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 4 ＝ 監査強度の分岐）: **機械検査を新設・改修する**（`check-reading-budget.js` 新設・`check-kit-sync.js` 差し替え）→ 全面 1 巡 ＋ 是正差分 1 巡

## 2. 母集合

### 2.1 pin の差分

`b640159..4d6a7d6` は **1 コミット**（`git -C planning log --oneline b640159..4d6a7d6`）。変更 7 ファイル: 運用ガイド §8 追記・glossary 4 語・環流記録 2 件・キット CLAUDE.md（測定コマンド）・`kit-sync-classification.example.json`（C の新定義）。**`projects/microservices-platform/` の差分は空**。

### 2.2 計画 ID レンジの引き直し（companion `traceability.repo.md` の義務）

| 種別 | 旧（走査基準 `b640159`） | 新（走査基準 `4d6a7d6`） | 差 |
| --- | --- | --- | --- |
| `FR` | `01..22` | `01..22` | 不動 |
| `UC` | `01..11` | `01..11` | 不動 |
| `SC` | `01..21` | `01..21` | 不動 |
| `ADR` | `0001..0046` | `0001..0046`（46 ファイル・欠番なし） | 不動 |

**4 種とも不動**（`git -C planning diff --stat b640159 4d6a7d6 -- projects/microservices-platform` が空であることでも裏取り）。別紙 `plan-id-range-history-annex.md` へ記録した。

### 2.3 分類 C の再判定の母集合

`scripts/kit-sync-classification.json` の C **17 件全件**。判定材料は**ファイルごとに** `wc -c`（キット / 本リポ）・`git diff --no-index --numstat`・キット版の置換点行の有無（`置換点` / `<作成者>` 等の grep）を引いた（§4 の表）。**issue 本文の名指し 2 件だけを見て終えていない。**

### 2.4 キット同期の母集合

pin 前進後に `node scripts/check-kit-sync.js` を実行（Windows のため当初は 108 件が偽 unclassified。キット版へ差し替え後に再実行）: **drift 0 / unclassified 0 / missing 0**（A 76 / B 26 / C 4 / 対象外 9）。逆方向（B のうち新キットとバイト一致になったもの）: **0 件**。

### 2.5 必読規約の母集合（[IADR-0200](../adr/IADR-0200_reading-budget-population-per-agent.md) 決定 1）

| 集合 | 実測（本 PR 完了時） | 判定 |
| --- | --- | --- |
| **Claude Code**（`CLAUDE.md` 23,198 ＋ `.claude/rules/traceability.md` 21,590 ＋ `.claude/rules/traceability.repo.md` 5,408） | **50,196 B（98.0%）** | **warn**（fail ではない） |
| AGENTS.md 系（`AGENTS.md`） | 4,710 B（9.2%） | ok（観測のみ） |
| Copilot（`.github/copilot-instructions.md`） | 2,850 B（5.6%） | ok（観測のみ） |

issue 起票時の実測 48,976 B（95.7%）から +1,220 B。内訳: CLAUDE.md +1,248（§8 母集合・§11 要点・companion への導線）／`traceability.md` −5,436（キット版）／companion +5,408。**余白 1,004 B（下限 1,000 B。[IADR-0190](../adr/IADR-0190_permanent-headroom-by-annexing-examples.md)）**。

### 2.6 「規則 7 / 8」の参照（改番の影響）

`grep -rn "規則 7\|規則 8"` で 20 箇所（`docs/adr/` 11・`docs/how-to/` 6・`scripts/scripts.repo.test.js` 3）。**書き換えたのは live な文書 3 件**（`population-drawing-annex.md`・`session-handoff.md`・テストの見出し）。**過去 IADR（0162 / 0166 / 0170 / 0174 / 0177 / 0185 / 0188 / 0189 / 0190 / 0197）は当時の番号のまま残す**（記録の改竄にあたる。別紙に「2026-08-16 より前の ADR が引く規則 7 / 8 は旧番号」と注記した）。

### 2.7 除外したものと理由

| 除外 | 理由 |
| --- | --- |
| `.claude/agents/spec-implementer.md` の母集合の表（規則 1〜6 のみ） | [IADR-0190](../adr/IADR-0190_permanent-headroom-by-annexing-examples.md) 決定 5 が射程外と定めており、規則 7〜10 の追加は別件 |
| `src/ai-stock-trading` submodule | 別リポの実体（pin `7f69fb5` 据え置き） |
| `docs/specs/`・`feedback/` の確定済み記録 | 一時点の記録（`traceability.repo.md` §Superseded の母集合の外） |
| #749 の是正 | 束ねない（§7） |

## 3. 変更内容

| # | 受け入れ基準（issue #755） | 変更 | 充足 |
| --- | --- | --- | --- |
| 1 | **pin 前進** | `planning` を `4d6a7d6` へ（独立コミット）。走査基準・別紙を追随 | ✅ |
| 2 | **必読規約の母集合（planning#364）** | `CLAUDE.md` §8 を「エージェントごとに分けて測り合算しない」へ改め、測定コマンド（`cat CLAUDE.md .claude/rules/*.md \| wc -c` ＝ Claude／`wc -c < AGENTS.md` ＝ 別枠）と予算値 **51,200**（正本: 運用ガイド §8）を明記。**`scripts/check-reading-budget.js` を新設**（AST の同名検査器を土台。母集合の定義と根拠をソース内に置く。100% fail / 90% warn / 欠落 missing。自己試験 15 件）。`ci.yml` `reading-budget` ジョブへ配線。**実測 50,196 B（98.0%）で warn 帯、exit 0** | ✅ |
| 3 | **分類 C の再判定（planning#363）** | `$comment` をキット example（pin 後）に揃え、C 17 件を全件再判定（§4）。名指し 2 件: `traceability.md` → **A**（companion `traceability.repo.md` へ固有分を退避。キット版をバイト一致で取り込み、是正 3 件が入った）／`check-cross-repo-refs.js` → **B（X）**（環境変数注入も置換点も使っておらずソース直書き。型 4 等の本リポ先行分があるため A にできない。追跡 #756） | ✅ |
| 4 | **§11 パリティ維持** | `CLAUDE.md`「実装作業の進め方」節へ §11 要点（配布点は kit に一本化／同時起票 issue の 7 日後突合／突合観点 6 種／定期監査の稼働確認）を追記。正本の日付を **2026-08-15** へ | ✅ |
| 5 | **check-kit-sync.js の Windows パス** | HOWTO の手順で優劣を再判定（§5）→ **キット版が優る**ため差し替え A。`listFiles` が `/` へ正規化（108 件の偽 unclassified 解消）。回帰テスト（`scripts.repo.test.js` #755 ①）で固定。#751 の `--require-planning` 追随と併せて解消（`check-feedback-status-sync.js` もキット版へ。`ci.yml` は自己試験のみ、実データ走査は `doc-links-planning.yml` へ） | ✅ |

### 3.1 #751 の受け入れ基準

| 基準 | 充足 |
| --- | --- |
| planning 未 populate ＋ `--require-planning` で exit 1 | ✅（`KIT_DIR=/no/such` / `PLANNING_FEEDBACK_DIR=/no/such` で実走。自己試験でも固定） |
| フラグ無しでは warn ＋ exit 0 | ✅ |
| 未知のフラグを黙って無視しない | ✅（`--requre-planning` → exit 1） |
| `doc-links-planning.yml` で実データ走査が実際に走ることをジョブログで確認 | ⏳ 夜間（17:00 UTC）または `workflow_dispatch` の初回実走で確認する（マージ後。IADR-0201 フォローアップ） |
| IADR-0192 決定 4 / IADR-0193 決定 3 の改定 | ✅（[IADR-0201](../adr/IADR-0201_class-c-rejudgement-and-fail-closed-kit-checks.md) 決定 4。両 ADR へ日付つき追記ブロック） |

## 4. 分類 C 17 件の再判定（結果表）

判定材料: キット版サイズ / 本リポ版サイズ / `numstat`（本リポ vs キット）/ キット版の置換点行。

| # | ファイル | 旧 | 新 | 根拠 |
| --- | --- | --- | --- | --- |
| 1 | `.claude/rules/traceability.md` | C | **A** | 置換点なし。固有節（+196/−109）は companion へ退避してバイト一致 |
| 2 | `.gitignore` | C | **B(2)** | キット 632B を全行保持 ＋ スタック固有 440 行 |
| 3 | `AGENTS.md` | C | **B(3)** | 置換点未充填。土台の「実装作業の進め方（要約）」節をキット版へ揃え、固有デルタは束ね範囲の 1 行（IADR-0116 / IADR-0139） |
| 4 | `CHANGELOG.md` | C | **B(3)** | 生成物（+370 行）。土台の見出し・注記はキット |
| 5 | `CLAUDE.md` | C | **B(2+1)** | 置換点「技術スタック別ルール」を埋めており C(b) も成立するが、土台の規約文はキットが正で追随対象のため B |
| 6 | `docs/README.md` | C | **B(3)** | 本リポの運用ルール（+46/−2）。キット example も B に置く |
| 7 | `docs/adr/README.md` | C | **C(b)** | 索引の置換点を埋めている（キット example も C） |
| 8 | `docs/ai-workflow.md` | C | **B(2)** | 必須チェック表・ワークフロー実名（+78/−68）。置換点は説明文中の引用のみ |
| 9 | `docs/operations/operations.md` | C | **C(b)** | 雛形（1,090B）から書き起こし `<作成者>` / `<YYYY-MM-DD>` を埋めた |
| 10 | `docs/security/security.md` | C | **C(b)** | 同上 |
| 11 | `docs/tech/tech-requirements.md` | C | **C(b)** | 同上 |
| 12 | `scripts/README.md` | C | **B(3)** | 本リポ固有スクリプトの行を追記（+126/−43）。表の土台はキット |
| 13 | `scripts/changelog-overrides.json` | C | **B(5)** | `overrides` 配列はキットが空で配り各リポが足す欄（第 5 種） |
| 14 | `scripts/check-commit-messages.js` | C | **B(X)** | 本リポ originate（#579 実在性検査・worktree-state）。追跡 #756 |
| 15 | `scripts/check-cross-repo-refs.js` | C | **B(X)** | 本リポ originate #507・型 4 #590。置換点も環境変数注入も使わずソース直書き（planning#363 名指し 2 件目）。追跡 #756 |
| 16 | `scripts/check-plan-id-qualification.js` | C | **B(X)** | 本リポ originate #576。キット版は置換点 `PROJECT_PREFIXES` の別実装。追跡 #756 |
| 17 | `scripts/scripts.test.js` | C | **B(X)** | 設計上 A（companion 方式）だがキット版が +750 行先行。キット版テストはキット版検査器を前提にするため単独では追随できない。追跡 #757 |

**集計**: A 73 → **76**（+traceability.md / check-kit-sync.js / check-feedback-status-sync.js）／B 16 → **26**／C 17 → **4**／対象外 9。X は 5 件（#749 / #756 ×3 / #757）。

## 5. `check-kit-sync.js` / `check-feedback-status-sync.js` の優劣判定（HOWTO の手順）

| 観点 | 本リポ版（5,731B / 10,333B） | **キット版（15,578B / 14,569B）** |
| --- | --- | --- |
| 3 点検査・0 件走査の門・分類 A 0 件の門 | あり | あり |
| パスの `/` 正規化 | **なし（Windows で 108 件偽陽性）** | **あり** |
| `--require-planning`（fail-closed） | なし | **あり** |
| 未知の引数の拒否 | なし | **あり** |
| `--self-test` | なし / あり（R1〜R5） | **あり（13 件 / 16 件）** |
| 探索先の上書き（`KIT_DIR` / `PLANNING_FEEDBACK_DIR`）・隣接クローン | なし | **あり** |
| 本リポ版にだけある機能 | **無し**（`compare()` の差はメッセージ文言と `#664` の出典表記のみ） | — |

→ **キット版が優る。差し替えて A。** 実データで実走: kit-sync `OK: キット 115 件…（A 76 / B 26 / C 4 / 対象外 9）`、status-sync `OK: 記録 46 件のうち 41 件を計画側と突合`。

## 6. 検証（実走した結果）

| 検査 | 結果 |
| --- | --- |
| `node scripts/check-reading-budget.js --self-test` | 15 件 all passed |
| `node scripts/check-reading-budget.js` | Claude Code 50,196 B（98.0%）warn／exit 0 |
| `node scripts/check-kit-sync.js` / `--self-test` | OK（A 76）／13 件 |
| `node scripts/check-feedback-status-sync.js` / `--self-test` | OK（41 件突合）／16 件 |
| `node scripts/check-test-traceability.js --self-test` | 34 件 OK（`RULES_FILE` を companion へ変更） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 結果は PR 本文（検査器の母集合ラチェット 36 → 37 が設計どおり発火） |
| `check-doc-links` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-adr-numbering` / `check-doc-status-vocabulary` / `check-doc-updated` / `check-commit-messages` | 結果は PR 本文 |

## 7. 束ねの判定（[IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md)）

- **#751 は束ねる。** #755 の受け入れ基準 5 が「#751 の `--require-planning` 追随と併せて解消」と明記し、同じ資源（キット同期検査器 2 本とその CI 配線）に閉じる。IADR-0139 の 6 条件は「裁定済みの同型な**契約追加**」向けで本件はメタ作業だが、条件 A（同一資源）・B（裁定済み＝planning#343）・D（1 コミット = 1 論点で分ける）・E（着手済みでない）は満たす。
- **#749 は束ねない。** 資源が別（`check-planning-pin-freshness.js`）で、条件 B を満たさない（案 A / 案 B / キット版差し替えの選択が未決）。分類表 B（X）の理由欄に「キット版への差し替えも俎上」を追記した。

## 8. この作業で扱わないこと

| 対象 | 理由 |
| --- | --- |
| #756（本リポ先行の検査器 3 本の優劣判定・環流） | 本 PR で起票。分類は B（X）で追跡先あり |
| #757（`scripts.test.js` のキット追随） | 同上 |
| #749 | §7 |
| `spec-implementer.md` への規則 7〜10 追加 | [IADR-0190](../adr/IADR-0190_permanent-headroom-by-annexing-examples.md) 決定 5 の射程外 |
| AGENTS.md 系の予算値の実測 | 観測のみ（planning#364） |
