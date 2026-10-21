---
title: E2E の基準書（docs/tests/E2E_STANDARDS.md）と実行層・live opt-in の IADR を置く（#2014 / E-00）
type: spec
status: done
related_ids: [NFR, ADR-0031, ADR-0032, ADR-0090, IADR-0569, IADR-0330, IADR-0337, IADR-0440, IADR-0442, IADR-0248]
author: claude
created: 2026-10-21
updated: 2026-10-21
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0032（SPA は BFF セッション。トークンを扱わない）
  - planning:projects/microservices-platform/07_adr/ADR-0090（門は依存ごと。全 skip を緑にしない）
issue: "#2014"
---

# 仕様書: E2E の基準書と実行層の定義（#2014 / E-00）

> 本仕様書は実装着手前に作成した（着手 2026-10-21）。基点は MSP `origin/develop` `7297af58`。判断は IADR-0569。
> 親 issue #2013（Playwright E2E の網羅）の波 0。ドキュメントだけの PR である（コード・設定・ワークフローは触らない）。

## 起点となる計画書（トレーサビリティ）

- 非機能: NFR（無採番。テスト基盤のメタ作業。該当する `NFR-xx` は無い）
- 関連 ADR: ADR-0032（SPA はトークンを扱わない）・ADR-0090（門は依存ごと・全 skip を緑にしない）・ADR-0031（表示言語）
- 関連 IADR: IADR-0330（L1 の BFF スタブ）・IADR-0337（全画面スモーク）・IADR-0440 / IADR-0442（axe）・IADR-0248（統合スタックは nightly・必須にしない）

## 範囲

| 成果物 | 内容 |
| --- | --- |
| `docs/tests/E2E_STANDARDS.md`（新規） | #2013 コメント 1（基準書）の §2〜§12 を docs 化。trace ブロック規約に従い、表示テキストから計画 ID・IADR・issue 参照・リポ外のファイル名（`issues.md` 等）を外す |
| `.ai-context/adr/IADR-0569_e2e-layers-and-live-opt-in.md`（新規） | 層・タグ・Playwright project 名・opt-in 変数名（`E2E_LIVE` / `E2E_LAYER`）を決定として確定 |
| `.ai-context/adr/README.md` | 索引へ 1 行 |
| `docs/tests/TEST_STRATEGY.md` | 「テスト種別と責務」表の E2E 行に基準書への参照を足す。trace ブロックへ IADR-0569 と本仕様書を足す |

対象外: AST（E-01 が別 PR で写す）。`playwright.config.ts` の project 改名・`playwright.live.config.ts` の新設（E-18 / E-31 / E-71）。

## 基準書の原文から変えた点（事実確認の結果）

| 箇所 | 原文 | docs に書いた形 | 根拠 |
| --- | --- | --- | --- |
| §8 axe のタグ | `wcag2a` `wcag2aa` `wcag21a` `wcag21aa` ＋ `best-practice`（「既存 IADR-0442」） | **現状**: `wcag2a` / `wcag2aa` / `best-practice`。**目標**: `wcag21a` / `wcag21aa` を足す（画面拡張の PR で入れる） | `a11y.smoke.spec.ts` L133 は `['wcag2a', 'wcag2aa', 'best-practice']`。IADR-0442 決定 3 が足したのは `best-practice` だけで、IADR-0440 も `wcag2a` / `wcag2aa` だけ。テスト名は「WCAG 2.1 A/AA」と称するが 2.1 で加わった達成基準のタグは評価していない |
| §8 走査面 | 「既存の SC-19 の例」 | 現状 6 面（共通シェル・検索・文書管理・存在秘匿の 404・個人資料の削除確認・共有先ダイアログ） | `a11y.smoke.spec.ts` の test 6 本を実測 |
| §3 project の置き場 | `playwright.config.ts` に `smoke` ＋ live 系 project を同居 | L1 は `playwright.config.ts`、live 系は別ファイル `playwright.live.config.ts` | コメント 3 §2.3 と E-31 の触るファイル領域が別ファイルを前提にしている。同居すると L1 の `webServer`（プレビュー起動）が live 実行にも掛かる |
| §3 L1 project 名 | `smoke`（既定） | `smoke`。現行の `chromium` は次に `playwright.config.ts` を触る PR で改名する | 本 PR は docs だけ。`chromium` を project 名として参照する箇所は 0 件（`frontend.yml` の `playwright install chromium` はブラウザ名） |
| §3 opt-in | `E2E_LIVE=1`。`trading` / `llm` は `E2E_LAYER` | `E2E_LAYER` の値域を `ro` `rw` `trading` `llm`（カンマ区切り・既定 `ro`）として閉じ、`rw` も明示選択にした | 基準書 §12 は rw を dispatch 入力に置き nightly は setup ＋ ro だけ。値域を閉じないと綴り違いが黙って 0 件になる |
| 全体 | `issues.md` `live-poc.md` `coverage-*.md`・memory への参照 | 本文から外し、「稼働中の PoC での流し方（別資料）」の言い方に留める | リポ内に実体が無い（`gen-knowledge-graph --check` と docs 規約） |

## 母集合（規則 9・10）

- E2E の行を持つ docs: `grep -rn "Playwright" docs/ --include=*.md` で引いた。規範を持つのは `docs/tests/TEST_STRATEGY.md`「テスト種別と責務」の E2E 行だけ。他（`docs/tests/SC-*.md` の個別ケース、`docs/how-to/frontend-conventions-annex.md`）は個別手順であり層の定義を持たないため追随しない。
- IADR 番号: develop の最大 0568。push 直前に開いている PR の最大を再確認する（並行の #2024 と衝突したら後発が改番）。
- 本 PR で新たに誤りになる自分の記述: TEST_STRATEGY の E2E 行の「統合スタックでの拡充は後続 issue」は、基準書が層を定めた後も「実装は後続」で正しいため残す。

## 受け入れ基準

- [x] `docs/tests/E2E_STANDARDS.md` が `check-trace-blocks.js` と `check-doc-type-vocabulary.js` を通る
- [x] IADR に層・タグ・project 名・opt-in 変数名（`E2E_LIVE` / `E2E_LAYER`）が決定として確定している
- [x] `gen-knowledge-graph.js --check`・`check-doc-links.js`・IADR 索引の同期・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が緑

## 検証記録

実行日 2026-10-21・基点 `7297af58`。

| コマンド | 結果 |
| --- | --- |
| `node scripts/check-trace-blocks.js` | OK（196 件） |
| `node scripts/check-doc-type-vocabulary.js` | OK（1719 件） |
| `node scripts/gen-knowledge-graph.js --check` | OK |
| `node scripts/check-doc-links.js` | OK（1773 件） |
| `node scripts/check-adr-numbering.js`（IADR 索引の同期） | OK（重複・欠番なし、索引と双方向一致） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 1125 tests passed |

変異（戻し済み）:

- 基準書の本文末尾へ可視の `SC-19` / `IADR-0569` を足す → `check-trace-blocks.js` が「本文に可視の参照が残っています」で赤。
- 基準書の `type:` を値域外（`e2e-standard`）にする → `check-doc-type-vocabulary.js` が赤。
