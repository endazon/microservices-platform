---
title: 作業仕様書 — T-25 の整数 ns の時計を整数 ms へ戻す（#1546・計画 ADR-0113 決定 4・6）
type: spec
status: done
related_ids:
  - SC-15
  - NFR-13
  - ADR-0108
  - ADR-0113
  - IADR-0463
  - IADR-0432
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0113_timing-judgement-rank-sum-test.md (Accepted 2026-09-26・決定 4・6)
  - planning:projects/microservices-platform/07_adr/ADR-0108_timing-samples-at-microsecond-resolution.md (Accepted・決定 1 に 2026-09-26 改訂注記)
related_specs:
  - 20260926_1525_timing-resolution-t25
  - 20260926_1542_plan-adr-range-0113
issue: "#1546"
---

# 作業仕様書 — T-25 の整数 ns の時計を整数 ms へ戻す

## 目的と射程

#1526（`33a21412`）は計画 ADR-0108 決定 1 に従い、T-25（リセット申請の所要時間）の標本を整数 ns の時計で測るようにした。
判定式（2 段）はそのままで、その組は系統差 0 でも実行の約 62% が `不合格` になる（#1526 の作業仕様書 §合成試験・planning#659）。

計画 ADR-0113（planning#661 でマージ・Accepted）は:

- **決定 4**: 「ADR-0108 決定 1（整数 ns の時計）は、本 ADR の判定式と同時に入れる。**時計の変更だけを先に入れない。**」
- **決定 6 の暫定手段**: 「**`develop` の現行（整数 ms・2 段の判定式）のまま運用する。** 1 ms 以下の系統差は見逃す。整数 ns の時計を先に入れない」

同 ADR §起案前の確認 は整数 ns の時計を「ブランチ上にあり、未マージ」と見ていたが、#1526 はマージ済みだった（#1541）。
**develop は決定 4 が禁じた組にある。判定式の実装（#1541）より先に、時計の変更だけを戻す。**

**射程**: #1526 のうち**時計**（`submitResetRequest` の計時・分解能の宣言・それを固定する自己試験）だけを #1526 の前の形へ戻す。
**戻さないもの**: 札 `[T-25]`（テスト仕様書の採番。planning#650 裁定 (a)）、受け入れの根拠の改め（ADR-0108 決定 2。ADR-0113 も改めない）、
判定関数の付帯（半格子の整数比較・格子の前提・`resolutionMs` の引数・段の内訳の行）—— 分解能 1 ms では判定を変えない（下の同値確認）。
判定式の順位和検定への変更は #1541 が持つ。

## 母集合（規則 1〜6・9・10）

**軸 1（誤りの側＝整数 ns の時計の実体と、それを現行として書く記述）**:

```console
$ git grep -n -E "整数 ns|hrtime|timingClockNs|elapsedMsBetween|NS_PER_MS|1e-6 ms|刻みは 1 ns|IADR-0463" -- . ':!src/ai-stock-trading' ':!CHANGELOG.md'
```

| ヒット | 扱い |
| --- | --- |
| `scripts/check-password-reset-mail.js`（`NS_PER_MS` / `timingClockNs` / `elapsedMsBetween` / 宣言 `1 / NS_PER_MS` / 冒頭・`selfControlStepMs`・`evaluateTimingConsistency`・`submitResetRequest` のコメント / 自己試験 2 件） | **対象** |
| `docs/screens/SC-15_password-reset.md:296`（「いまは単調時計の整数 ns で測り、各群 3 標本では刻みは 1 ns」） | **対象** |
| `docs/tests/SC-15_password-reset.md:61`（T-25 行「単調時計の整数 ns で測る —— 刻みは 1 ns」） | **対象** |
| `scripts/README.md:44`（「標本は単調時計の整数 ns で測る」・自己試験 57 件） | **対象** |
| `.ai-context/adr/IADR-0463`（決定 1・2） | **対象**（本文は凍結。日付つき追記） |
| `.ai-context/adr/IADR-0432:313-327`（#1525 追記「時計は IADR-0463 が持つ」） | **対象**（日付つき追記で差し戻しを指す） |
| `.ai-context/adr/README.md:543`（IADR-0463 の索引行） | **除外**（据え置き）。行末へ注記を足すと索引行の長さの上限（`scripts.repo.test.js` の `title-too-long`）を超えた。索引は決定の要約であり、差し戻しは本体の日付つき追記と IADR-0432 の追記が持つ |
| `scripts/check-login-existence-disclosure.js:312,314`（`process.hrtime.bigint()`） | **除外**。ログイン経路は所要時間を出すだけで判定しない（ADR-0094 決定 4 の対象外。T-12）。ADR-0113 決定 4 の射程外 |
| `.ai-context/specs/20260926_1525_timing-resolution-t25.md`・`20260925_457_…`・`20260926_1521_…` | **除外**。確定済みの作業仕様書（当時の記録） |
| `.ai-context/specs/20260926_1542_plan-adr-range-0113.md:31` / `docs/how-to/plan-id-range-history-annex.md:45` | **除外**。本件を「差し戻す」と書く記述で、誤りではない |

**軸 2（札・ワークフロー・マニフェストのコメント）**: `git grep -n "T-25" -- .github deploy` —— `integration-stack.yml:92,93,95,96,231,240`・`reset-floor.yaml:34` は札 `T-25` と床の記述だけで時計に触れない。**除外**（戻さない側）。

**軸 3（刻み・分解能の値を持つ文書）**: `git grep -n -E "刻み（?1 ?ms|分解能 1 ms|整数 ms" -- docs scripts/README.md` —— SC-15 の 2 文書の「整数 ms の標本・各群 3 標本では 1 ms」は #1526 で「分解能と群の大きさから導く」へ一般化されており、戻した後も正しい。**据え置き**。

**規則 10（この変更で新たに誤りになる自分の記述）**: 自己試験の件数 57 → 56（分解能 1 ns の実時計の試験 1 件を撤去）。`scripts/README.md` の件数を直した。
ADR-0108 決定 2 由来の「検出できる下限は時計の分解能と標本数で決まる」（SC-15 画面仕様書）は ADR-0113 決定 5 が改めたが、それは判定式の変更（#1541）が持つ。本 PR では触らない。

## 変更

1. `scripts/check-password-reset-mail.js`
   - `submitResetRequest`: `Date.now()` の差へ戻す（#1526 の前と同じ 2 行）
   - `TIMING_SAMPLE_RESOLUTION_MS = 1`。`NS_PER_MS` / `timingClockNs` / `elapsedMsBetween` を撤去
   - 自己試験: 「`TIMING_SAMPLE_RESOLUTION_MS === 1`（`Date.now()` の差は整数 ms である）」と「現行構成の刻み ＝ 1」を #1526 の前の形へ戻し、
     整数 ns の実時計を固定していた試験（`🔴 T-25 分解能: 宣言は時計の単位（整数 ns）から導かれ…`）を撤去。
     分解能 1 ns の境界試験 5 件と「同じ形を 1 ns で判定すると段 2」は `resolutionMs: NS_GRID`（1e-6）を明示して残す（判定関数の性質の固定）
   - コメント: 冒頭の軸の説明・`selfControlStepMs`・`evaluateTimingConsistency`・`submitResetRequest` の「整数 ns」を「整数 ms（ADR-0113 決定 4・6）」へ
2. `docs/screens/SC-15_password-reset.md` / `docs/tests/SC-15_password-reset.md`: 「いまは整数 ns」→「判定式の変更までは整数 ms。整数 ns は判定式と同時に入れる」。trace ブロックに ADR-0113・本仕様書・#1546・planning#659
3. `scripts/README.md`: T-25 の説明と自己試験件数
4. `IADR-0463`（日付つき追記・related_ids に ADR-0113）/ `IADR-0432`（日付つき追記）

## 検証

### 合成試験（系統差 0 の不合格率）

#1526 の作業仕様書 §合成試験 と**同じ模型・同じ seed**（床 150 ms ＋ |N(0, sd)|、実在・非実在とも同じ分布から独立、反復 3〔暖機 1〕× 片側 6、
各 5,000 実行、mulberry32 seed 1000〜1003）。整数 ms の標本は開始の位相を U[0, 1) で取り `floor(開始 ＋ 所要) − floor(開始)`。
**検査器の判定関数を `resolutionMs` を渡さずに（＝検査器の既定の分解能で）呼ぶ**。スクリプトは scratch（`rs-sim-revert.js`）に置き、コミットしない（#1526 の仕様書のスクリプトと同じ本体）。

| sd | develop（整数 ns の時計・`origin/develop` の検査器） | 本 PR（整数 ms・差し戻し後） | #1526 の直前（`33a21412~1`） |
| --- | ---: | ---: | ---: |
| 20 µs | 不合格 63.4% | **0.0%** | 0.0% |
| 100 µs | 62.0% | **0.0%** | 0.0% |
| 300 µs | 62.4% | **0.0%** | 0.0% |
| 1 ms | 62.0% | **2.9%** | 2.9% |

`評価不能` はいずれも 0.0%。

### 判定の同値（#1526 の直前の検査器と）

整数 ms の合成入力 40,000 件（実在側のずれ 0〜3 ms・揺れ幅 1〜6 ms を一様に取る。seed 42）を両方の検査器の判定関数へ渡した:
`inputs=40000 mismatches=0 verdicts={"不合格":26062,"合格":13938}`。
分解能 1 ms では半格子の整数比較は `diff <= step` と同値であり、格子の前提は `Date.now()` の差（整数）では発火しない。

### 実行したコマンド

- `node scripts/check-password-reset-mail.js --self-test` → `self-test OK: 56 件`
- `node scripts/check-trace-blocks.js` / `gen-knowledge-graph.js --check` / `check-doc-links.js` / `check-cross-repo-refs.js` / `check-plan-id-qualification.js` / `scripts.test.js`（結果は PR 本文）

## 受け入れ基準

- [x] `--self-test` が緑（56 件）
- [x] 分解能の宣言が 1 ms で、`submitResetRequest` が `Date.now()` を使う
- [x] 系統差 0 の不合格率が 0〜3%（上表）
- [x] 所要時間の失敗の札は `[T-25]` のまま（自己試験 `所要時間の札が T-25 でない` が残る）

## 残るもの

- 🔴 **整数 ms の間は 1 ms 以下の系統差を見逃す**（段 1 が刻み 1 ms で合格させる。ADR-0113 実測 2）。ADR-0113 決定 6 が暫定手段として受け入れた状態であり、#1541 が解く。
- 自己試験は `submitResetRequest` がどの時計を使うかを見ない（#1526 の前と同じ）。時計だけを ns へ替えれば、格子の前提（決定 4）が実行時に全標本を不合格にする。
