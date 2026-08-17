---
title: 作業仕様書 — 波 10 末クロス監査の是正 6 件
type: spec
status: done
related_ids:
  - ADR-0030
  - NFR
  - IADR-0117
  - IADR-0141
  - IADR-0183
  - IADR-0218
  - IADR-0219
author: claude
created: 2026-08-17
updated: 2026-08-17
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/12_backend-application-stack.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md"
related_specs:
  - "20260817_planning-pin-767a9d48.md"
  - "20260817_iadr-0219_sharedkernel-worker-amendment.md"
  - "20260817_gitkeep-standard-components-apply.md"
  - "../adr/IADR-0219_sharedkernel-granularity-and-worker-standard-component.md"
---

# 作業仕様書: 波 10 末クロス監査の是正

## 1. 起点

波 10（#833 / #837 / #838）のマージ後、**書いた側とは別の、フレッシュな文脈のエージェント 2 種**
（`adr-guardian` / `traceability-auditor`）へ **diff と受け入れ基準だけを渡して**監査させた
（実装者の主張は渡していない）。**証跡（実行コマンドと生出力）を必須**とした。

**両監査とも「重大な違反 0 件」**である。`IADR-0117` の却下理由は**削除行 0 行**で保存され、
`.gitkeep` 56 件は `IADR-0219` 決定 3 の内訳と一致し、確定済み `docs/specs/` の書き換えも 0 件であった。
本書はそのうえで挙がった**追随漏れ 6 件**を是正する。

## 2. 是正する 6 件

| # | 指摘 | 種別 | 対応 |
| --- | --- | --- | --- |
| **A** | `docs/tech/tech-requirements.md:150`「**適用は未実施**」が偽 | **重大** | 「適用済み（#838）」へ |
| **B** | `docs/adr/IADR-0117:193` `Superseded by: なし` に部分改定の注記が無い | 中 | 注記を追加 |
| **C** | `docs/adr/README.md` の `IADR-0117` 索引行に改定注記が無い | 中 | 既存文言を圧縮して追加 |
| **D** | 入口 `traceability.repo.md:15` の AST レンジが実測と不一致・走査基準が到達不能 | 推奨 | `FR-01..21` / pin `767a9d48` へ |
| **E** | `docs/adr/IADR-0218` frontmatter の `plan_refs` に誤引用の痕跡 | 軽微 | `選定基準 1〜4` へ |
| **F** | `IADR-0218` 決定 4 の「そもそも未適用」が #838 以後は現在形として偽 | 軽微 | 日付つき追記（決定は変えない） |

### A —— **同じ波の中で規則 10 が破れた**（最も重い）

**#837 が書いた記述を #838 が偽にし、追随していなかった。**

- #837 が `tech-requirements.md:150` に「**適用は未実施**であり、次の作業で行う（対象 55 件）」と書いた
- #838 が**その適用を実行した**（`.gitkeep` 55 件 ＋ 雛形 1 件）
- **#838 は `src/README.md` を直したが `tech-requirements.md` を引き直さなかった**

**#838 の作業仕様書 §5「追随不要と判断したもの」に `tech-requirements.md` が載っていない** ——
母集合に入っていなかった。`未実施|未適用|次の作業で行う|次の波` の 4 語で引けば 1 発で捕まる型である。

**これは規則 10（是正のたびに「この変更で新たに誤りになる自分の記述」を引き直す）そのものの破れ**であり、
**同じ波の中で起きた**という点で悪い。走査は「是正前の語」（`.gitkeep` / `SharedKernel`）では捕まらず、
**自分が直前に書いた「未実施」という語**で引く必要があった。

**再走査の結果**（本 PR で実施）:

```bash
git grep -n --untracked -E "未実施|未適用|次の作業で行う|次の波" -- . ':!planning' ':!src/ai-stock-trading'
```

`tech-requirements.md:248` にも「未実施」があるが、**負荷試験（#196）についてであり本件と無関係**なので触らない。

### D —— **AST の採番が伸びていたことに誰も追随していなかった**

入口 `traceability.repo.md:15` は「AST の採番は `FR-01..20`（pin `655e2ed`）」と書いていた。**両方とも誤り**である。

```bash
$ grep -oP '^\| FR-[0-9]+' planning/projects/ai-stock-trading/02_requirements/01_requirements.md \
    | grep -oP 'FR-[0-9]+' | sort -u -V | tail -1
FR-21          # ← 2026-08-07 裁定で新設・2026-08-08 改定

$ git -C planning cat-file -t 655e2ed
fatal: Not a valid object name 655e2ed     # EXIT=128（どの ref からも到達できない）
```

**走査基準が追試できない sha であることは、レンジの主張そのものを検証不能にする。**

#### ★ 副次的な発見 —— **別紙の分類が失効していた**

`docs/how-to/cross-project-id-refs-annex.md:52` は `FR-21` を「**MSP にしか無い**（AST の採番は
`FR-01..20` なので衝突しない）」と分類していた。**AST が `FR-21` を採番した以上、この分類は失効し、
`FR-21` は同紙が定義する「誤帰属」型へ移った。**

**同じ行が「将来 AST が採番を伸ばせば上の『誤帰属』型へ移る」と自ら予告しており、
その予告どおりのことが起きたのに誰も移していなかった。** 本 PR で移す。

#### 追随不要と判断したもの（**実測して確かめた**）

| | 理由 |
| --- | --- |
| `scripts/check-test-traceability.js:435` / `scripts/scripts.repo.test.js:903` の `FR-01..20` | **合成フィクスチャであり実データではない。** パーサが「起点 ID の種別（固有）」節だけを見て AST 行を拾わないことを固定する試験であり、**MSP 行と AST 行が別の値であること自体が試験の目的**である。実在のレンジに合わせる必要は無く、合わせると試験の意味が薄れる |
| `docs/specs/*` の `FR-01..20`（5 ファイル） | **`status: done`・走査基準つきの過去の実測**。書き換えない |

## 3. R-3 —— **確定済み仕様書の誤りを、書き換えずにここへ記録する**

`docs/specs/20260817_planning-pin-767a9d48.md`（**`status: done`・#833 でマージ済み**）§2.2 の内訳表に
**誤りが 1 件**ある。**同書は書き換えない**（`traceability.repo.md`「確定済みの `docs/specs/` は書き換えない」）ため、
**訂正をここへ残す。**

| | 同書の記述 | 実測 |
| --- | --- | --- |
| 旧 pin sha `8cae89d` のヒット総数 | 13 ファイル | **13**（一致） |
| うち `docs/specs/*` | **7 ファイル** | **5 ファイル** |

```bash
$ git grep -l '8cae89d' 26e45293 -- . ':!planning' ':!src/ai-stock-trading' | wc -l
13
$ git grep -l '8cae89d' 26e45293 -- . ':!planning' ':!src/ai-stock-trading' | grep -c 'docs/specs/'
5
```

**表の中だけで検算が破れていた** —— 非 `docs/specs/` の相異なるファイルは 8 件で、8 + 7 = 15 ≠ 13。
**5 なら 8 + 5 = 13 で合う。** 規則 8（走査がそのまま返す数を先に出し、引き算を見せる）の面の破れである。

**是正の判断そのものは正しかった**（13 件それぞれの「直す / 直さない」の判定に取り違えは無いことを、
監査が 1 件ずつ突き合わせて確認している）。**誤っていたのは内訳の数だけ**である。

## 4. 監査が「欠陥は無い」と確認した項目（記録）

- **`IADR-0117` の却下理由**: `git diff | grep -c "^-[^-]"` = **2**（frontmatter のみ）。**本文の削除 0 行**
- **`ADR-0030` 誤引用**: 訂正漏れ 0 件・**過剰是正も 0 件**（`IADR-0216` の正しい引用と確定済み spec は無傷）
- **`.gitkeep`**: 56 件を要素別に数え直して `IADR-0219` 決定 3 と一致。**全件 0 バイト**・`.csproj` 増減 0・`src/ai-stock-trading` 無変更
- **`Api`/`Worker` の排他**: 9 + 2 = 11 で **11/11 成立**
- **確定済み `docs/specs/`**: 変更 0 件（3 件はすべて新規追加 `A`）
- **旧 pin sha の「直す / 直さない」判定**: **取り違え 0 件**
- **他リポジトリ ID の修飾**: 違反 0 件。検査器が見ていない 2 面（`scripts/` の非 Markdown 73 件・submodule 2 本）を手走査しても実違反 0 件

## 5. 検証

**[[IADR-0183]] の順序**（`git add -A` → 検査器 → コミット → HEAD を読む検査器）。

- **索引タイトルは事前に機械で検算する**（上限 200 字・本体との LCS 12 以上）
- **必読規約を増やさない**（`traceability.repo.md` の変更は `FR-01..20` → `..21` が ±0、sha が +1）
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` を**必ず回す**
- **終了コードは判定ではない。判定行を読む**
