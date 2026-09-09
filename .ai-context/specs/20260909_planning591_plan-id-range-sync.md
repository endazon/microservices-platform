---
title: 作業仕様書 — 計画 ID レンジを ADR-0001..0093 へ前進させ、公開 kg-ranges.json との突合を新設する
type: spec
status: done
related_ids: [NFR, ADR-0048, ADR-0093, IADR-0228, IADR-0200]
author: endazon (with Claude Code)
created: 2026-09-09
updated: 2026-09-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0093_plan-id-ranges-derived-and-published.md
  - planning:projects/microservices-platform/07_adr/ADR-0048_impl-docs-restructure.md
  - planning:tools/doc-checks/kg-ranges.json
---

# 作業仕様書 — 計画 ID レンジを `ADR-0001..0093` へ前進させ、公開 `kg-ranges.json` との突合を新設する

> 対象: planning#591 Q2 の裁定（案 A）と計画 `ADR-0093` のフォローアップ 1・2 の**本リポジトリ分**。
> ai-stock-trading 側は AST#721 で先行して着地させた。**本作業はその移植である。**

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（メタ作業。NFR）
- ユースケース（UC）/ 画面（SC）: なし
- 関連 ADR: `ADR-0093`（計画 ID レンジは実物から導出して公開し、実装リポジトリはそれへ追随する。Accepted 2026-09-09）／`ADR-0048` 決定 2（planning 依存の禁止。`ADR-0093` 決定 3 が範囲を 4 点に限って部分改定した）
- 関連 IADR: `IADR-0228`（planning 依存の撤去。pin は本作業でも復活させない）
- 計画書リンク: 隣接クローン `../project-planning` の読み取り、または GitHub URL（submodule は張らない）

## 目的・背景

`.claude/rules/traceability.repo.md`「起点 ID の種別（固有）」節の宣言は `check-trace-blocks.js` /
`check-commit-messages.js` の**一次情報**である。**宣言が計画側の実物より遅れている間、そのレンジ外の
ID を引く PR は CI が落ちて通らない。**

### 🔴 実測（2026-09-09。計画リポの `node tools/doc-checks/gen-plan-ranges.js --check`）

| 種別 | 本リポの宣言 | 計画側の実物 |
| --- | --- | --- |
| FR | `FR-01..22` | [1, 22] ✅ |
| UC | `UC-01..11` | [1, 11] ✅ |
| SC | `SC-01..21` | [1, 21] ✅ |
| **ADR** | **`ADR-0001..0088`** | **[1, 93]**（93 件・欠番なし） |

**ADR が 5 件遅れている。** 前回の前進（`0086 → 0088`・#1333・2026-09-08）から 1 日で 5 件動いた。

### 🔴 転記元を計画 ADR の本文にしない

計画 `ADR-0093` のフォローアップ 1 は「MSP `..0088` → **`..0092`**」と書くが、
**その `ADR-0093` 自身が加わって実物は `0093` である。**
**ADR 本文の数値は、その ADR が着地した時点で古くなる。** 本作業は**実測値**を採る。

### 検知の穴

前進の契機は従来「自分の作業が新しい ADR を引いて `check-trace-blocks` に止められたとき」であった
（別紙 `plan-id-range-history-annex.md` の 2026-09-08 エントリ）。**これは事後検知であり、
止まった PR の作業者がその場で追随させる形になる。** 計画側が `ADR-0093` 決定 1 で
`kg-ranges.json` を公開する成果物へ格上げしたため、**先回りして突合できるようになった。**

## 対象範囲

- **対象**
  - `.claude/rules/traceability.repo.md:7` の計画 ADR レンジを `ADR-0001..0088` → `ADR-0001..0093` へ
    （🔴 **数字の置換だけにとどめ、必読規約のバイト数を 1 バイトも増やさない**。理由は下記「必読予算」）
  - 別紙 `docs/how-to/plan-id-range-history-annex.md` へ本世代の引き直し記録を追加
  - `scripts/check-planning-adr-range.js` を**新設**（AST#721 からの移植。FR/UC/SC/ADR の 4 種・`scanned` 併記）
  - `ci.yml` の `static-checks` へ自己試験と本検査を配線
  - `scripts/README.md` へ 1 行追加
- **対象外**
  - `NFR` のレンジ突合（`ADR-0093` 決定 2 で「レンジ表へ足さない。追加の可否は別途の裁定による」）
  - 終了コードを 0 以外にすること（`ADR-0093` 決定 3「落とし方は警告に限り、ビルドやテストの前提にしない」）
  - `lib/plan-ranges.js` の新設（**本リポには不要**。`check-trace-blocks.js` が `planAdrRange` を既に公開しており、**同じ事実を 2 本のパーサで持たない**）

## 設計

```
fetchPlanningRanges()   gh api repos/endazon/project-planning/contents/tools/doc-checks/kg-ranges.json
                        （Accept: application/vnd.github.raw。読み取り専用 HTTP・1 回）
                          ↓  microservices-platform キー
                        { FR:[1,22], UC:[1,11], SC:[1,21], ADR:[1,93] }

readDeclaredRanges()    FR/UC/SC … check-test-traceability.js の readPlanIds()
                        ADR     … check-trace-blocks.js の planAdrRange()   ← 🔴 既存の公開関数を再利用
                          ↓
compareRanges()         種別ごとに ok / behind / ahead ＋ scanned（比較できた種別数）
```

- **常に exit 0**（fail-open）。secret 不在・API 失敗・宣言不読はいずれも `status: "unverified"` ＋ 理由。
- 🔴 **`scanned` を必ず併記する。** `scanned: 0` は「ずれが無い」ではなく「**検査が動いていない**」である
  （`ADR-0093` 決定 3。同決定が言う「fail-open のままにしない」の内容は走査件数の併記である）。
- **`behind` を `ahead` より優先**（前進漏れのほうが実害＝レンジ外 ID を引く PR の CI 落ちを起こす）。
- **AST 版との差は宣言の読み手だけ**である（AST は `lib/plan-ranges.js`、本リポは `check-trace-blocks.js` の `planAdrRange`）。**キットとの乖離は受容する**（`ADR-0048` 決定 6）。

### 🔴 必読予算（着手中に実測して設計を変えた）

**当初は「転記元は実測である」という注記を宣言の隣へ書いた。** 実測したところ **190 バイト増で
`46,076 → 46,270`（89.99% → 90.4%）となり、warn 閾値（90%）を新たに越えた。**
短縮しても `46,092`（90.02%）で越えたままだった。

**したがって注記は別紙へ置き、宣言側は数字の置換だけにする**（`CLAUDE.md` のラチェット運用
「足すときは同量を削るか別紙へ落とす」）。宣言の行は既に「引き直しの記録は別紙」と別紙を指しており、
**読み手はそこから辿れる。** 結果は **`46,076` バイト・純増 0**。

🔴 **上限（51,200）にはまだ 5KB 余裕があるのに足せない。** 効いている制約は**警告の閾値**のほうである。
**上限との差だけを見て「入る」と判断しない。**

## 受け入れ基準

- [x] `.claude/rules/traceability.repo.md:7` の計画 ADR レンジが `ADR-0001..0093` になっている
- [x] 別紙に本世代（`0088 → 0093`）の引き直し記録がある
- [x] `node scripts/check-planning-adr-range.js --self-test` が全件 pass（陽性対照を含む）
- [x] secret 不在で `--out` を実行すると exit 0・`status: "unverified"`・`scanned: 0`・理由つき JSON
- [x] `ci.yml` の `static-checks` に自己試験と本検査が配線されている
- [x] `node scripts/check-trace-blocks.js` / `check-commit-messages.js` が通る
- [x] `node scripts/check-reading-budget.js` が **warn を増やさない**（純増 0 バイト。実測 `46,076`）
- [x] `node scripts/scripts.test.js` が通る

**実測（2026-09-09。すべてこのブランチで実走）**: 自己試験 14 件合格 / `scripts.test.js` **769 件合格** / `check-trace-blocks` 173 件・違反 0 / `check-doc-links` 1,285 件・破損 0 / `check-reading-budget` 46,076 バイト・**純増 0**（warn なし）/ `check-adr-numbering` 重複欠番なし / `check-cross-repo-refs` 3,416 件・違反 0 / `check-plan-id-qualification` 2,839 件・違反 0 / `check-workflow-job-refs` 一致 / `gen-knowledge-graph --check` 違反 0 / `check-commit-messages --range origin/develop..HEAD` 適合。

## テスト方針

- 自己試験に**陽性対照**（ずらしたら落ちる）を必ず置く。「一致なら ok」だけでは比較が空振りでも緑になる。
- **本番の抽出関数そのものを呼ぶ。** パーサを試験側へ書き写さない。
- ネットワークは叩かない（`execFn` / `fetchFn` を差し替える）。
- 🔴 **自己試験に「現在の宣言値」をリテラルで書かない。** 書くと、次にレンジを前進させる PR で
  自己試験まで同時に直さないと `ahead` で落ちる —— **本検査器が塞ごうとしている「導出値の書き写し」
  そのものである**（母集合の規則 10）。実物の宣言を使う 2 件は**配線が通ること**だけを固定し、
  **ずれの検出はフィクスチャによる陽性対照が持つ。** 実際のずれは CI の本走が公開ファイルと突き合わせる。

## 計画書との差異

- 差異: **あり（数値のみ）。** 計画 `ADR-0093` フォローアップ 1 は `..0092` と書くが実物は `0093`。
  **環流は不要** —— 同 ADR 自身が「ずれは正常な作業で広がる」と明記している。

## 母集合（規則 1・2・9。誤りの側＝`ADR-0001..0088` の文字列で引いた）

```
grep -rn 'ADR-0001\.\.' . --exclude-dir=.git --exclude-dir=node_modules --exclude-dir=ai-stock-trading -I
  → 該当は 1 件のみ: .claude/rules/traceability.repo.md:7
```

**他の一致はすべて別世代の値**（`.ai-context/specs/` の凍結記録・`CHANGELOG.md` の生成物・
別紙の履歴・検査器の書式説明 `ADR-0001..NNNN`・テストの合成フィクスチャ）であり、
**いずれも当時の値を書いているため直さない。**

### 除外したものと理由（規則 6）

| 除外 | 理由 |
| --- | --- |
| `.ai-context/specs/` / `.ai-context/adr/` | **凍結記録**。本文プロズを後から書き換えない |
| `CHANGELOG.md` | 生成物。コミット件名を書き換えず `changelog-overrides.json` で是正する |
| `docs/how-to/plan-id-range-history-annex.md` の既存エントリ | **履歴そのもの**。過去世代の値を直すと履歴が消える。**追加**する |
| `scripts/scripts.repo.test.js` / `check-test-traceability.js` の `ADR-0001..0039` / `..0068` | **合成フィクスチャ**であり実ファイルを読んでいない（先例: `20260830_issue-1060_plan-adr-range-0066.md`） |

### 同型の穴の引き直し（規則 10）

**是正後の語（`0093`）でも引き直す。** AST 側では回帰テストが `ADR-0035` / `ADR-0036` を直書きしており
前進で落ちた。**本リポにも同型が無いか、番号の直書きで引き直した** ——
`grep -rn 'ADR-0089\|ADR-0090\|ADR-0091\|ADR-0092\|ADR-0093' scripts/` の一致は
**すべて `IADR-`（実装 ADR）であり計画 ADR ではない**（`IADR-0088` / `IADR-0089` / `IADR-0090`）。
**本リポの回帰テストは計画 ADR 番号を直書きしていない。**

## 未決事項

- なし（`NFR` をレンジ表へ足すかは計画側の別裁定。`ADR-0093` フォローアップ 3）
