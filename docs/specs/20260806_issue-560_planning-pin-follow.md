---
title: 作業仕様書 — planning pin を planning#206 / #207 へ進める
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

# 作業仕様書 — planning pin を planning#206 / #207 へ進める（#560）

## 目的

計画リポジトリの `main` が `e36b592` まで進み、**本リポジトリ発の環流 3 件が反映された**。
実装リポジトリの pin を追随させ、**何が変わり・何が変わっていないか**を記録する。

## 着手時の実測

### 実測 1: 差分は 2 コミット。MSP に関わるのは 1 本目だけ

| commit | 内容 | MSP 関連ファイル |
| --- | --- | --- |
| `57adb9d` | planning#206 — open issue の整理。**環流 planning#201 / #202 / #203 の反映**と対応表の同期 | `06_technical/14_knowledge-graph-graphrag.md` / `07_adr/ADR-0035_*.md` / `07_adr/README.md` |
| `e36b592` | planning#207 — AST の moomoo PoC 結果 | **なし**（別プロジェクト） |

環流 3 件はいずれも本リポジトリ発である。

- planning#201 → 画面仕様書の対応表を 3 値へ（実装側の受け皿は **#552**）
- planning#202 → `pr-title.yml` の bot 除外（**#524 で解消済み**）
- planning#203 → ABAC 属性組み合わせ数の実測結果（**#456 / #515** の成果）

### 実測 2: ID レンジは変わっていない

```console
$ ls planning/projects/microservices-platform/07_adr/ | grep -oE 'ADR-[0-9]{4}' | sort -u | tail -1
ADR-0043
```

`ADR-0001..0043`（欠番なし）で **planning#200 時点から不変**。planning#206 / #207 は既存 ADR の更新と
AST 側の反映であり、新規 ADR を起こしていない。

### 実測 3: `ADR-0035` に実測が反映されたが、**状態は `Proposed` のまま**

```console
$ grep -m1 '^status:' planning/.../07_adr/ADR-0035_graphrag-retrieval-strategy.md
status: Proposed
```

保留注記は次のように更新された（原文）。

> ~~本 ADR は ABAC 属性組み合わせ数の実測を経ずに起案している~~ **一部解消・保留は継続（2026-08-06）**。
> 実測は 2026-08-05 に実施され、**決定 3 は実測に対して安全側**であることが確認された。ただし
> **その測定は本番相当ではない**（データ源が単一で属性の多様性が 1 通りに留まる）ため、
> **稼働後の再実測までは保留を続ける**。

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
| `feedback/` の status 同期 | 本 pin で計画側の状態が動いた記録があれば追随が要るが、**#497 と同じ全数突合が要る**ため混ぜない | 別 issue（未起票） |

## 検証（実測）

| 検査 | 結果 |
| --- | --- |
| `node scripts/check-doc-links.js` | green |
| `node scripts/check-doc-links.js --dir feedback` | green（**既定は `docs/` しか走査しない**——`ci.yml` も `doc-links-planning.yml` も `--dir` を渡さないため、pin がずれても CI では検出されない。#497 の変異試験 M2 で判明した穴） |
| `node scripts/check-commit-messages.js --base origin/develop` | green |
| `git diff --submodule=diff -- planning` | **pin のみの変更**（内容差分なし） |
| `git ls-tree HEAD -- planning` と `git submodule status` の一致 | 一致（**pin 退行なし**） |

> **リポジトリ全体を数える値は固定値で書かない。** 他 PR のマージで必ず動く（#497 / #520 / #512 で
> 連続指摘された型）。**本作業固有の不変量**（`planning/` の内容差分が無いこと・ID レンジが不変であること）
> だけを残す。

## 未決事項・親への申し送り

1. **`feedback/` の status 追随** —— 本 pin で計画側 draft の状態が動いた記録があるかは未確認。#497 と同じ
   全数突合が要るため本作業には混ぜていない。**未起票。**
2. **`ADR-0035` の再実測** —— 稼働後に本番相当のデータで測り直すまで保留が続く。**実環境が要る**ため
   実装セッションからは着手できない（#456 と同じ制約）。
3. **`feedback/` が CI のどの経路でも検査されない** —— 上表のとおり。**#523 の申し送りとして既出**で、
   結線には `.github/workflows/` の編集が要る（権限外）。
