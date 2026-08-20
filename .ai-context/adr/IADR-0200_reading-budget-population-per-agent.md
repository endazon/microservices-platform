---
title: IADR-0200 必読規約の総量予算はエージェントごとの母集合で測り、検査器が出典つきの予算値を持つ
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0178
  - IADR-0188
  - IADR-0190
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - planning:docs/ai-implementation-workflow-guide.md (§8 予算値の正本)
  - planning:draft/feedback/20260815_reading-budget-mother-set-undefined.md (裁定 planning#364)
  - planning:tools/impl-handoff-kit/repo-template/CLAUDE.md (測定コマンド)
related_specs:
  - "../specs/20260816_issue-755_planning-pin-4d6a7d6-catchup.md"
---

# IADR-0200: 必読規約の総量予算 —— 母集合はエージェントごと、予算値は出典つきの複製（#755）

- 状態: Accepted
- 日付: 2026-08-16
- 決定者: 計画側の裁定（planning#364）＋ claude（実装）

## 起点・関連

- **NFR**（文書統制。**当たる番号が無い＝場合 ②** なので無採番・環流しない。[IADR-0189](./IADR-0189_follow-upstream-adjudication-in-kit.md) 決定 1）
- 実装 issue: **#755**（計画 pin `4d6a7d6` の追随。AST#524 と対）
- 作業仕様書: [20260816_issue-755](../specs/20260816_issue-755_planning-pin-4d6a7d6-catchup.md)
- 改定対象: [IADR-0178](./IADR-0178_claude-md-defers-to-docs-readme.md) 決定 4／[IADR-0188](./IADR-0188_unnumbered-nfr-applies-to-all-work.md) 決定 4 が定めた「必読 2 ファイル（`CLAUDE.md` ＋ `.claude/rules/traceability.md`）で 50,000B」の測り方
- 土台: ai-stock-trading の同名検査器（`AST/IADR-0204`。planning#364 の裁定は「実データが在る場所で作る」として実装側の検査器を待った）

## 文脈 —— **母集合が定義されておらず、合算して 2 回誤った**

運用ガイド §8 は「毎セッション必読の規約は総量 50KB 予算」と定めるが、**何を足すか**は定めていなかった。
実装側（ai-stock-trading）は 2 回とも `AGENTS.md` を合算して 90% 超と報告し、**「90% を超えたら着手する」という着手条件の判定まで狂わせた**（正しくは 82.7%）。planning#364 がこれを裁定した（2026-08-15・pin `4d6a7d6`）。

本リポの測り方にも同じ穴があった。回帰テスト（#724 / #730）は **`REQUIRED = [traceability.md, CLAUDE.md]` をリテラルで列挙**しており、`.claude/rules/` に companion（[IADR-0201](./IADR-0201_class-c-rejudgement-and-fail-closed-kit-checks.md) で作った `traceability.repo.md`）を置いても**黙って母集合から落ちる**。実際、本 PR で companion を足した瞬間に旧テストは「予算内」のまま通った —— 母集合が縮んだことに気づけない形である。

## ★★ 決定 1: **母集合は「そのエージェントが自動で読み込む集合」であり、エージェントごとに分けて予算と比べる。合算しない**

| 集合 | 中身 | 根拠 |
| --- | --- | --- |
| **Claude Code** | `CLAUDE.md` ＋ **`.claude/rules/*.md` を走査**（列挙しない） | CLAUDE.md「Claude はこのファイルを毎セッション読み込む」／同ディレクトリの `*.md` は自動適用（companion 機構） |
| AGENTS.md 系（Codex / Cursor / Aider） | `AGENTS.md` | AGENTS.md 冒頭「Claude 以外の AI エージェントが読み込む共通指示」。**Claude は読まない** |
| GitHub Copilot | `.github/copilot-instructions.md` | CLAUDE.md「Copilot 固有の運用は `.github/copilot-instructions.md`」 |

- **submodule（`planning/` / `src/ai-stock-trading/`）配下の `CLAUDE.md` は入れない**（そのディレクトリで作業するときだけ読まれる）
- **制約は「セッション 1 本が背負う量」に掛かる。合算は「誰も背負わない量」を作る**（裁定 planning#364）
- **定義と根拠は検査器 `scripts/check-reading-budget.js` のソース内に置く**（別ファイルへ置くと定義と実装がずれても誰も気づかない）

## ★★ 決定 2: **予算値は 51,200 バイト。正本は計画リポ運用ガイド §8 であり、検査器は出典つきの複製を持つ**

- 従前の回帰テストは `50000` を持っていた。**運用ガイドは「50KB = 51,200」**と書いており、キット `CLAUDE.md` の測定コマンドも `51200` を目安にする。**値は正本へ揃える。**
- 検査器・テストが値を持つのは**複製として認めるが、値の隣に出典を書く**（planning#364。出典の無い複製は認めない）。**外部依存ゼロを保つほうが、submodule 未取得の CI で黙って skip するより堅い**（正本を実行時に読みに行かない）。
- **`AGENTS.md` 系は実測が無いため同じ値を暫定的に流用し、超過を fail の根拠にしない**（観測に留める）。実測が揃った時点で別に定める。

## ★★ 決定 3: **100% 超で fail、90% 以上で warn。warn は失敗にしない。欠落は missing として出す**

- 超えてから気づくと、減量を迫られる場面で減らせるものが残っていない。**接近を warn で見せる。**
- **存在しないファイルを黙って 0 として扱わない** —— 落とすと「集合が縮んだ」ことに気づけず、予算に収まったように見える。
- 実測（本 PR 時点）: **Claude Code 50,196 バイト（98.0%）→ warn**。内訳 `CLAUDE.md` 23,198 / `traceability.md`（キット）21,590 / `traceability.repo.md` 5,408。issue 起票時の実測 48,976 B（95.7%）から、§8・§11 の追記と companion 分離で +1,220 B。**[IADR-0190](./IADR-0190_permanent-headroom-by-annexing-examples.md) の余白下限 1,000B は保っている**（余白 1,004B。回帰テスト #730 が固定する）。

## 決定 4: **CI へ配線する（`ci.yml` の `reading-budget` ジョブ）。回帰テストは検査器の `measure()` を呼び、リテラルの一覧・値を持たない**

- `#724` / `#730` の回帰テストは `check-reading-budget.js` の `AGENT_SETS` / `BUDGET_BYTES` / `measure()` を使う形へ書き換えた。**同じ事実を 2 箇所に持たない。**
- テストは **companion が母集合に入っていること**（走査で拾えていること）も確かめる。

## 検討した選択肢

| | A. エージェントごとに分ける（採用） | B. 従前どおり 2 ファイルを列挙 | C. 全部合算 |
| --- | --- | --- | --- |
| companion を足したとき | 走査で拾う | **黙って落ちる**（本 PR で再現） | 拾う |
| 着手条件の判定 | 正しい | 正しい（が母集合が縮む） | **狂う**（実測 2 回） |
| 正本との整合 | 51,200・出典つき | 50,000（正本と 1,200 ずれ） | — |

## 結果

- 良い影響: 母集合が検査器に定義され、companion を足しても落ちない。予算値が正本と揃った。
- 悪い影響・トレードオフ: 実測 98.0% で warn 帯にある。**次に規範を足すときは同量を削るか別紙へ落とすことが要る**（[IADR-0190](./IADR-0190_permanent-headroom-by-annexing-examples.md) の余白は 1,004B）。AGENTS.md 系の予算は暫定である。
- フォローアップ: AGENTS.md 系の実測が揃ったら別の値を定める（planning へ環流）。

## 検出しないこと（明示する）

- **内容の妥当性**（規範が正しいか）。見るのはバイト数だけである。
- **手動で読まれる文書**（`docs/README.md`・別紙 `docs/how-to/*-annex.md`）。母集合は「自動で読み込まれる集合」に限る。
