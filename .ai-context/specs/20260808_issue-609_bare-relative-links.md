---
title: 作業仕様書 check-doc-links.js が同一ディレクトリのベア相対リンクを検査するようにする（#609）
type: spec
status: done
related_ids: [NFR, IADR-0140, IADR-0141]
author: Claude
created: 2026-08-08
updated: 2026-08-08
plan_refs: []
related_specs:
  - 20260808_issue-514_strict-decision.md
  - ../adr/IADR-0141_audit-rounds-and-population-drawing.md
---

# 仕様書: ベア相対リンクを検査対象にする（#609）

> **本作業は「違反を直す」作業ではない** —— 実データの破損は 0 件である。
> 作るのは**壊れたときに止まる仕組み**であり、**今まで一度も見ていなかった 248 件**を検査下に入れる。

## 起点となる ID（トレーサビリティ）

- 起点 issue: **#609**／起点 ID: **NFR**（追跡可能性・退行防止）
- **発見の経緯**: PR #607（[IADR-0130](../adr/IADR-0130_test-spec-coverage-ratchet.md) の追補）の作業中。§関連 へ足したリンクの**ファイル名を間違えた**
  （`IADR-0138_error-message-spec-scope.md`。実在は `IADR-0138_coverage-exclude-generated-code.md`）のに、
  `node scripts/check-doc-links.js` が**緑のままだった**。手で `ls` して初めて気づいた。
- 分類（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 4）: **「機械検査を新設・改修する」** —— クロス監査は**フェーズ末に 1 回**
  （同 決定 4 の 2026-08-08 追記）
- 規約: `.claude/rules/traceability.md`

## 原因（実測）

`isBrokenRef()`（`scripts/check-doc-links.js`）の相対リンク判定:

```js
const looksRelative = t.startsWith('./') || t.startsWith('../') || (t.includes('/') && !t.startsWith('/'));
if (!looksRelative) return false;
```

**`./` `../` で始まるか `/` を含むものしか相対リンクと見なさない。**
同一ディレクトリのベアファイル名は `/` を含まないので、**この時点で素通りする。**

```
$ node -e "const m=require('./scripts/check-doc-links.js'); console.log(m.isBrokenRef('IADR-0138_error-message-spec-scope.md','docs/adr'))"
false        # ← 実在しないのに「破損でない」
```

**これは [IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) の「走査式が返さなかった」は「存在しない」を意味しない、の実例である。**
`OK: 461 件の Markdown に破損した相対リンクはありません` という出力は、
**461 件を見た**とは言っているが、**各ファイルのどのリンクを見たか**は言っていない。

## 母集合の引き直し（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 1）

**走査基準**: `origin/develop` = `bf5a2c3`（#610 マージ後）。

**誤りの側から引いた** —— 「壊れているリンク」ではなく「**判定を素通りする形のリンク**」を数えた。

| 軸 | 対象 | 実測 |
| --- | --- | ---: |
| 本文の Markdown リンク `](...)` のうち `/` を含まず拡張子を持つもの | 追跡下の `*.md` | **248 件** |
| うち現時点で実在しないファイルを指すもの | 同上 | **0 件**（#607 の 1 件を直した後） |
| 是正後に新たに赤になったもの（実走） | 同上 | **0 件** |

### 引いた軸と、引かなかった軸

| 軸 | 引いたか | 理由 |
| --- | --- | --- |
| 本文の Markdown リンク | ✅ | 穴を踏んだ経路そのもの |
| **frontmatter の ID リスト**（`related_specs` 等） | ✅ | 同じ `isBrokenRef` を通る。`docs/adr/` の `related_specs` は**実際にベア名で書かれている**（例 [IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md)） |
| **インラインコード内の相対パス** | ❌ | 経路 3 は `./` `../` で始まるものだけを拾う設計で、**ベア名はそもそも収集されない**。ここを広げるとコード中の識別子（`Foo.md` 風の語）を拾い始めるので**広げない** |
| **submodule 配下・`planning/`** | ❌ | 未 populate。従来どおり除外し、除外件数を出力へ残す |

## やること

1. `looksRelative` へ「**`/` を含まず `LINK_EXT` に掛かるベアファイル名**」を足す。
2. **自己試験へ正例・負例を対で足す**（下記）。
3. 実データで違反 0 件を確認し、**変異で赤になることを実証する**。

### 誤検出をどう抑えたか

**`LINK_EXT` が唯一の門番である。** 拡張子を持たない語（`README`）や `Foo.Bar` のような識別子は
`LINK_EXT` に掛からないので、ベア名でも相対リンクとして扱われない。
**新しい除外リストは作らない** —— 既にある絞り込みで足りるところへ 2 本目の基準を置くと、
どちらが効いているのか読めなくなる。

## 変異試験（実測）

| 変異 | 結果 |
| --- | --- |
| **実データの実在リンクを 1 本壊す**（`docs/adr/IADR-0130_*.md` の `IADR-0118_backend-coverage-floor.md` → 不在名） | **exit 1**。`docs/adr/IADR-0130_test-spec-coverage-ratchet.md` / `- IADR-0118_does-not-exist.md` を出力 |
| 復元 | exit 0 |
| 是正前に同じ変異を当てる | **exit 0（緑）** ＝ これが穴 |

## 自己試験に足した対（**この対が無かったことが穴を長く開けたままにした直接の原因**）

| 種別 | 内容 |
| --- | --- |
| 正例 | 同一ディレクトリの**実在**ファイルをベア名で指す → 破損でない |
| 負例 | 同一ディレクトリの**不在**ファイルをベア名で指す（`.js`） → 検出する |
| 負例 | 同上（`.md`。**ADR の §関連 で実際に踏んだ型**） → 検出する |
| 誤検出しない | 拡張子を持たない語（`README` / `IADR-0138`） → 相対リンクと見なさない |
| 誤検出しない | 対象外拡張子の識別子（`Foo.Bar` / `*.txt`） → 検出しない |

## 新 IADR の要否: **不要**

**既存の設計判断を変えていない** —— `check-doc-links.js` は「相対リンクの実在を見る」道具であり、
本変更は**その意図どおりに動いていなかった箇所を直す**ものである。新 IADR を起こすと
「何を相対リンクと見なすか」の参照点が 2 つに割れる（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md)「参照点を 1 つに畳む」）。
誤検出の抑え方（`LINK_EXT` を唯一の門番にする）は本仕様書とスクリプト内コメントを正とする。

## 受け入れ基準（#609）

- [x] ベア相対リンクが検査対象になり、**変異（実在するリンクを 1 本壊す）で赤になることを実証した**
- [x] 自己試験に正例・負例が対で常設されている（39 件 OK）
- [x] 実データ（追跡下の全 `*.md`）で違反 0 件
- [x] 誤検出の実測結果（拡張子なしの語・識別子を拾っていないこと）が本書に残っている

## 検証

```
node scripts/check-doc-links.js --self-test        # 自己試験 39 件 OK
node scripts/check-doc-links.js                    # OK: 464 件
node scripts/check-plan-id-qualification.js
node scripts/check-cross-repo-refs.js
REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js  # 288 tests passed
```
