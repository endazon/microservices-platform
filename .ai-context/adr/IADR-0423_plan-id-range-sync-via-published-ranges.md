---
title: IADR-0423 計画 ID レンジの突合を新設し、出典を計画側が公開する kg-ranges.json に置く
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0048, ADR-0093, IADR-0228, IADR-0200]
author: endazon (with Claude Code)
created: 2026-09-09
updated: 2026-09-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0093_plan-id-ranges-derived-and-published.md
  - planning:projects/microservices-platform/07_adr/ADR-0048_impl-docs-restructure.md
  - planning:tools/doc-checks/kg-ranges.json
---

# IADR-0423: 計画 ID レンジの突合を新設し、出典を計画側が公開する `kg-ranges.json` に置く

- 状態: Accepted
- 日付: 2026-09-09
- 決定者: Claude Code（計画 ADR-0093 Accepted のフォローアップ 1・2 の本リポジトリ分。裁定は planning#591 で受領済み）

## 起点・関連

- 関連計画書 ID: ADR-0093（計画 ID レンジは実物から導出して公開し、実装リポジトリはそれへ追随する）／ADR-0048 決定 2（planning 依存の禁止。ADR-0093 決定 3 が範囲を 4 点に限って部分改定した）
- 対象 issue: planning#591（裁定依頼 Q2・案 A で確定）
- 関連する実装仕様書: `20260909_planning591_plan-id-range-sync`
- 関連 IADR: IADR-0228（planning 依存の撤去。pin は本作業でも復活させない）／IADR-0200（必読規約の予算）
- 先行実装: ai-stock-trading の IADR-0320（AST#721）。**本 IADR はその移植である。**

## コンテキストと課題

`.claude/rules/traceability.repo.md`「起点 ID の種別（固有）」節の宣言は `check-trace-blocks.js` /
`check-commit-messages.js` の**一次情報**である。宣言が計画側の実物より遅れている間、
**そのレンジ外の ID を引く PR は CI が落ちて通らない。**

### 🔴 実測 1 —— 1 日で 5 件遅れた

| 種別 | 是正前の宣言 | 実物（計画リポ `gen-plan-ranges.js --check`・2026-09-09） |
| --- | --- | --- |
| FR / UC / SC | `01..22` / `01..11` / `01..21` | いずれも一致 |
| **ADR** | **`0001..0088`** | **`0001..0093`**（93 件・欠番なし） |

前世代（`0086 → 0088`・#1333）は 2026-09-08 である。**1 日で 5 件動いた。**

### 🔴 実測 2 —— 従来の検知は事後だった

別紙 `docs/how-to/plan-id-range-history-annex.md` の各世代が記録するとおり、前進の契機は
**「自分の作業が新しい ADR を引いて `check-trace-blocks` に止められたとき」**であった。
**止まった PR の作業者がその場で追随させる形**であり、先回りする手段が無かった。

### 🔴 実測 3 —— 計画 ADR の本文を転記元にすると、その時点で既に古い

計画 ADR-0093 のフォローアップ 1 は本リポジトリを `..0092` と書くが、
**その ADR-0093 自身が加わって実物は `0093` である。**
**ADR 本文の数値は、その ADR が着地した時点で古くなる。**

## 検討した選択肢

| # | 案 | 評価 |
| --- | --- | --- |
| 1 | **公開 `kg-ranges.json` を 1 回取得し、FR/UC/SC/ADR の 4 種を突き合わせる**（ai-stock-trading IADR-0320 の移植） | **採用**（決定 1〜3）。計画側が ADR-0093 決定 1 で同ファイルを「実物からの導出結果を公開する成果物」へ格上げし、`gen-plan-ranges.js --check` を CI の必須チェックに置いた。**取得 1 回で 4 種が見える** |
| 2 | `07_adr/` のディレクトリ一覧を取り ADR だけ比較する（ai-stock-trading の旧実装） | 採らない。FR/UC/SC のずれを検知できず、**導出規則を本リポジトリ側にも持つことになる** |
| 3 | 何もせず、従来どおり `check-trace-blocks` に止められたら直す | 採らない。**事後検知であり、止まるのは無関係な作業の PR である。** 計画側は既に公開の側を整えた |
| 4 | ずれ検出で exit 1 にする | 採らない。計画 ADR-0093 決定 3 が「**落とし方は警告に限り、ビルドやテストの前提にしない**」と定める |

## 決定

### 決定 1: レンジ宣言を `ADR-0001..0093` へ前進させる。**宣言側は数字の置換だけにする**

🔴 **出典は計画リポの `node tools/doc-checks/gen-plan-ranges.js --check` の出力であり、計画 ADR の本文ではない**（実測 3）。

🔴 **その旨は宣言の節ではなく別紙へ置いた。必読予算に入らないためである**（実測 4）。宣言の行は既に「引き直しの記録は別紙」と別紙を指しており、読み手はそこから辿れる。**宣言側の変更は `0088` → `0093` の数字だけで、純増 0 バイトである。**

### 🔴 実測 4 —— 上限にはまだ 5KB 余裕があるのに、注記を足せなかった

当初は注記を宣言の隣へ書いた。実測すると **190 バイト増で `46,076` → `46,270`（89.99% → 90.4%）となり、
warn 閾値（90%）を新たに越えた。** 短縮しても `46,092`（90.02%）で越えたままだった。

**効いている制約は上限（51,200）ではなく警告の閾値のほうである。** **上限との差だけを見て「入る」と判断しない**
（`CLAUDE.md` のラチェット運用「足すときは同量を削るか別紙へ落とす」の実例）。

### 決定 2: `scripts/check-planning-adr-range.js` を新設し、出典を公開 `kg-ranges.json` に置く

- 取得は `gh api repos/endazon/project-planning/contents/tools/doc-checks/kg-ranges.json`（`Accept: application/vnd.github.raw`）の 1 回。token は env `PLANNING_REPO_TOKEN` を子プロセスの `GH_TOKEN` へ写す。
- 🔴 **宣言の読み手はパーサを書き写さない。** FR/UC/SC は `check-test-traceability.js` の `readPlanIds()`、**計画 ADR は `check-trace-blocks.js` が既に公開している `planAdrRange()`** を再利用する。**`lib/plan-ranges.js` を新設しない** —— ai-stock-trading にはあるが本リポには無く、**同じ事実を 2 本のパーサで持たないほうが優先する**（`check-commit-messages.js` が `readPlanIds()` を再利用しているのと同じ理由）。**キットとの乖離は受容する**（ADR-0048 決定 6）。
- 🔴 **`NFR` は突き合わせない**（ADR-0093 決定 2。検査の射程を黙って広げない）。
- **`behind` を `ahead` より優先**して報告する（前進漏れのほうが実害を起こす）。

### 決定 3: `scanned` を必ず併記し、終了コードは変えない

- `scanned` は突き合わせられた種別の数である。🔴 **`scanned: 0` は「ずれが無い」ではなく「検査が動いていない」。**
- **常に exit 0。** secret 不在・API 失敗・宣言不読はいずれも `status: "unverified"` ＋ 理由。ADR-0093 決定 3 が言う「fail-open のままにしない」の内容は、**同決定の本文どおり走査件数の併記**である。

### 決定 4: `ci.yml` の `static-checks` へ自己試験と本検査を配線する

**ジョブは増やさない**（Node の軽量検査は `static-checks` / `static-checks-units` の 2 ジョブへ束ねる方針。IADR-0232 決定 6）。**step 名をブランチ保護へ指定しない。**

## 結果

### 実測した変異（陽性対照）

| 変異 | 期待 | 実測 |
| --- | --- | --- |
| 宣言の ADR だけ `0088` へ戻す | `behind`・指摘は ADR の 1 種だけ | 一致 |
| 宣言の SC だけ `1..20` へ戻す | `behind`（**ADR だけを見る実装では検知できない形**） | 一致 |
| 計画側のレンジ表を空にする | `scanned: 0`・`unverified` | 一致 |
| secret を外して実バイナリを実行 | exit 0・`unverified`・`scanned: 0`・理由に `PLANNING_REPO_TOKEN` | 一致 |

自己試験 14 件すべて合格。

🔴 **陽性対照はフィクスチャで書き、「現在の宣言値」をリテラルで書かない**（AI レビューの指摘で是正）。
実物の宣言を使う 2 件は**配線が通ること**（4 種そろってパースでき、比較器が処理できること）だけを固定する。
**現在値をリテラルと突き合わせると、次にレンジを前進させる PR で自己試験まで同時に直さないと `ahead` で落ちる**
—— **本検査器が塞ごうとしている「導出値の書き写し」そのものである**（母集合の規則 10）。
**実際のずれは CI の本走が公開ファイルと突き合わせる。**

### 残るもの

- 🔴 **`PLANNING_REPO_TOKEN` が本リポジトリに登録されていなければ、CI では常に `unverified` になる。** その状態は `scanned: 0` として出るため「動いていない」と読めるが、**登録するまで突合は実際には効かない。** 登録は権限を持つ人の作業である。
- 🔴 **突合は警告のみである。** 前進させるのは依然として人（または後続 PR）であり、**宣言する側が本リポジトリにある構造は変えていない。**
- **`NFR` はレンジ検査の対象外のままである**（計画側の別裁定待ち。ADR-0093 フォローアップ 3）。
- **ai-stock-trading 版とはパーサの読み手だけが違う。** 同じ事実の実装が 2 リポジトリに並ぶため、**片方だけ直すと乖離する**。乖離に気付いたら受容として記録する（ADR-0048 決定 6）。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連 IADR: IADR-0200（必読規約の予算。本作業は必読側を +約 0.2KB だけ動かす）／IADR-0232 決定 6（Node の軽量検査は `static-checks` へ束ねる）
