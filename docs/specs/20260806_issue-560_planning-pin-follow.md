---
title: 作業仕様書 — planning pin を planning#206 / planning#207 へ進める
type: spec
status: done
related_ids: [NFR, FR-17, FR-18, FR-19, FR-20]
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0035_graphrag-retrieval-strategy.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0043_scoped-attribute-value-lookup.md"
  - "../../planning/projects/microservices-platform/06_technical/14_knowledge-graph-graphrag.md"
related_specs:
  - "../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md"
author: endazon
created: 2026-08-06
updated: 2026-08-06
---

# 作業仕様書 — planning pin を planning#206 / planning#207 へ進める（#560）

## 目的

計画リポジトリの `main` が `e36b592` まで進み、**本リポジトリ発の環流 3 件が反映された**。
実装リポジトリの pin を追随させ、**何が変わり・何が変わっていないか**を記録する。

## 着手時の実測

### 実測 1: 差分は 2 コミット。MSP に関わるのは 1 本目だけ

| commit | 内容 | MSP 関連ファイル |
| --- | --- | --- |
| `57adb9d` | planning#206 — open issue の整理。**環流 planning#201 / planning#202 / planning#203 の反映**と対応表の同期 | `06_technical/14_knowledge-graph-graphrag.md` / `07_adr/ADR-0035_*.md` / `07_adr/README.md` |
| `e36b592` | planning#207 — AST の moomoo PoC 結果 | **なし**（別プロジェクト） |

**「MSP 関連ファイル」欄には `projects/microservices-platform/` 配下しか書いていない。** `57adb9d` は
これとは別に **キット配布物 `tools/impl-handoff-kit/repo-template/` を 5 本**変更している
（`.claude/rules/traceability.md` / `.github/workflows/pr-title.yml` / `docs/templates/screen_spec_template.md` /
`scripts/check-commit-messages.js` / `scripts/scripts.test.js`）。本リポジトリは `CLAUDE.md` 冒頭のとおり
同キットから生成されているため、**キット配布物は本リポの直接の上流**であり、追随要否の判断が要る。
本 pin では 5 本とも下記の環流 3 件に由来し、**実装側の追随要否はすでに判断済み**である
（planning#202 → #524 で解消済み／planning#201 → #552 が受け皿）。

環流 3 件はいずれも本リポジトリ発である。

- planning#201 → 画面仕様書の対応表を 3 値へ（実装側の受け皿は **#552**）
- planning#202 → `pr-title.yml` の bot 除外（**#524 で解消済み**）
- planning#203 → ABAC 属性組み合わせ数の実測結果（**#456 / #515** の成果）

### 実測 2: ID レンジは変わっていない

**最大値だけでは「欠番なし」を示せない**ので、番号を全数集めて欠番を出す。

```console
$ ls planning/projects/microservices-platform/07_adr/ | grep -oE 'ADR-[0-9]{4}' | sort -u \
    | node -e 'const n=require("fs").readFileSync(0,"utf8").trim().split("\n").map(s=>+s.slice(4));
               const miss=[];for(let i=1;i<=Math.max(...n);i++)if(!n.includes(i))miss.push(i);
               console.log("count:",n.length,"max:",Math.max(...n),"missing:",JSON.stringify(miss))'
count: 43 max: 43 missing: []
```

`ADR-0001..0043`（欠番なし）で **planning#200 時点から不変**。planning#206 / planning#207 は既存 ADR の更新と
AST 側の反映であり、新規 ADR を起こしていない。

### 実測 3: `ADR-0035` に実測が反映されたが、**状態は `Proposed` のまま**

```console
$ grep -m1 '^status:' planning/.../07_adr/ADR-0035_graphrag-retrieval-strategy.md
status: Proposed
```

保留注記は次のように更新された（原文。末尾の 1 文と出典表記を省略している）。

> ~~本 ADR は ABAC 属性組み合わせ数の実測を経ずに起案している~~ **一部解消・保留は継続（2026-08-06）**。
> 実測は 2026-08-05 に実施され、**決定 3 は実測に対して安全側**であることが確認された。ただし
> **その測定は本番相当ではない**（データ源が単一で属性の多様性が 1 通りに留まる）ため、
> **稼働後の再実測までは保留を続ける**。（…以下略: 「（§結果 の追記）」と
> 「決定 3 は設計上の上限で代替しており、再実測で見直す可能性がある。」）

**保留理由のうち片方は解消している。** 起案時の保留理由は 2 つあり、理由 1（`/sync-impl` による実装 IADR
との突合が未了）は planning#206 で**完全に解消**した（同 commit の記録で「実装 IADR: **突合済み**」・
IADR 134 件・指摘なし）。したがって `Accepted` を妨げる要因は**稼働後の再実測ただ 1 つに絞られている**。

**「実測が反映された」ことと「着手条件を満たした」ことは別である。**
[[IADR-0119]] の着手条件は前提 ADR が `Accepted` であることなので、
**#450（FR-17/18）・#451（FR-19/20）の保留は解除されない。**

### 実測 4: `ADR-0043` は `Accepted`

```console
$ grep -m1 '^status:' planning/.../07_adr/ADR-0043_scoped-attribute-value-lookup.md
status: Accepted
```

**#540（権限内属性値の照会 API）の着手条件は満たされている。**

## 対象範囲

### 対象

- `planning` submodule の pin: `5f1bd63` → `e36b592`
- `.claude/rules/traceability.md` の pin 参照と、`ADR-0035` の状態に関する日付つき追記

### 対象外（送り先を明記する）

| 対象外 | 理由 | 送り先 |
| --- | --- | --- |
| `planning/` の内容変更 | `CLAUDE.md` の規約。実装ブランチで許されるのは **pin 更新のみ** | — |
| 画面仕様書の対応表 3 値化 | planning#201 の実装側の受け皿は別 issue | **#552** |
| `feedback/` の status 同期 | 本 pin で計画側の状態が動いた記録があるため追随が要るが、**#497 と同じ全数突合が要る**ため混ぜない | **#563** |

## 検証（実測）

| 検査 | 結果 |
| --- | --- |
| `node scripts/check-doc-links.js` | green |
| `node scripts/check-doc-links.js --dir feedback` | green（**既定は `docs/` しか走査しない**——`ci.yml` も `doc-links-planning.yml` も `--dir` を渡さないため、pin がずれても CI では検出されない。#497 の変異試験 M2 で判明した穴） |
| `node scripts/check-commit-messages.js --base origin/develop` | green |
| `git diff --name-only origin/develop...HEAD` | `.claude/rules/traceability.md` / `docs/specs/20260806_issue-560_planning-pin-follow.md` / **`planning`（1 エントリのみ）** = pin のみの変更 |
| `git ls-tree HEAD -- planning` と `git submodule status` の一致 | 一致（**pin 退行なし**） |

> **「pin のみの変更」の根拠に `git diff --submodule=diff -- planning` を使ってはならない。**
> このコマンドは**範囲指定が無く worktree と HEAD を比べる**ため、作業ツリーが clean なら常に空になり、
> ブランチ差分の性質を何も示さない（当初この行を根拠として書いていた。マージ前監査で反証された）。
>
> ```console
> $ git diff --submodule=diff -- planning | wc -l
> 0        # ← 何も検査していないのに「差分なし」に見える
> $ git diff --submodule=diff origin/develop...HEAD -- planning | wc -l
> 1163     # ← 範囲を付けると planning 側の内容差分がこれだけある（pin が前進したのだから当然）
> ```
>
> 示したいのは「**この PR が `planning/` 配下のファイルを書き換えていない**」ことなので、
> 見るべきは `git diff --name-only origin/develop...HEAD` に **`planning` が 1 エントリだけ現れ、
> `planning/...` のパスが 1 件も現れない**ことである。

> **リポジトリ全体を数える値は固定値で書かない。** 他 PR のマージで必ず動く（#497 / #520 / #512 で
> 連続指摘された型）。**本作業固有の不変量**（`planning/` の内容差分が無いこと・ID レンジが不変であること）
> だけを残す。

## 未決事項・親への申し送り

1. **`feedback/` の status 追随** —— マージ前監査で全数突合を実施した結果、**同名で status が食い違う記録が
   12 件**あり、うち 2 件（`20260805_abac-attribute-combination-measurement-result.md` /
   `20260805_kit-pr-title-bot-author-gate.md`）は**本 pin の前進そのものが新たに作ったずれ**である。
   さらに `20260804_sc01-03-bff-contract-gaps.md` は計画側で `20260805_` 前綴へ**改名**されており、
   ファイル名で突き合わせる限りこのずれは永久に見えない（実質 13 件）。**#563 へ切り出した。**
   本作業に混ぜないのは #497 と同じ全数突合＋個別判断（1 件は向きが逆で impl=`rejected` / plan=`open`）が
   要るためである。
2. **`ADR-0035` の再実測** —— 稼働後に本番相当のデータで測り直すまで保留が続く。**実環境が要る**ため
   実装セッションからは着手できない（#456 と同じ制約）。
3. **`feedback/` が CI のどの経路でも検査されない** —— 上表のとおり。**#523 の申し送りとして既出**で、
   結線には `.github/workflows/` の編集が要る（権限外）。
4. **列挙形の修飾漏れを止める機械が 1 つも無い** —— 本 PR は当初、`planning#206 / #207` ／
   `planning#201 / #202 / #203` と書いており、**`.claude/rules/traceability.md` が「列挙形でも各番号を
   修飾する」と定めている当のファイルの中で、その規約に違反していた**（9 occurrence）。マージ前監査で
   検出して是正したが、`check-commit-messages.js` は件名の**書式**しか見ないため green のまま通り抜けた。
   裸の `#207` は本リポジトリの無関係な issue へ自動リンクする実害がある。検査の新設は **#507**
   （「可能なら機械検査へ載せる」）の範囲として追記した。
