---
title: IADR-0167 `type` の値域はテンプレートを実行時に読んで決め、1 枚のテンプレートを 2 種別で共用しない
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0130
  - IADR-0166
author: claude
created: 2026-08-10
updated: 2026-08-10
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
---

# IADR-0167: 仕様書 `type` の値域をどう決めるか（#675）

- 状態: Accepted
- 日付: 2026-08-10
- 決定者: claude（実装）

## 起点・関連

- **NFR**（文書統制）。実装 issue: **#675**（**自分で起票した**。出所は #667 / [IADR-0166](./IADR-0166_status-vocabulary-and-record-rewrite-boundary.md) 決定 5）
- 作業仕様書: [20260810_issue-675](../specs/20260810_issue-675_spec-type-vocabulary.md)

## 文脈 —— **起票の「三つ巴」が誤りだった**

#675 は「正本が三つ巴（テンプレの `spec` ／ 種別表の `work` ／ 実データの `work-spec`）」と書いた。**誤りである。**

`.claude/commands/new-spec.md` の種別表は **`/new-spec` の引数の語彙**であり、`type` の値ではない。
**表は「引数・文書名・テンプレート・出力先・粒度」の 5 列で、`type` の列は無い。**
**`type` を書いているのはテンプレートだけ**である。

**19 種別すべてで引数と `type` は機械的に対応していた**（実測）:
15 種別が `<引数>-spec`、例外は `work`→`spec` ／ `adr`→`impl-adr` ／ `runbook`→`operations-spec` ／
`how-to`→`spec` の 4 つ。**三つ巴ではなく二層であり、#675 は 2 つの層を 1 軸に潰して数えていた。**

> **★ #667 に続き 2 回連続で、自分が起票した issue の前提が誤っていた。**
> 前回は「規定が無い」（実際はあった）、今回は「三つ巴」（実際は二層）。
> **どちらも起票時に一次資料を開かず、記憶で構造を述べていた。**

## ★ 決定 1: **`type` の正本はテンプレートである。値域はハードコードしない**

`scripts/check-doc-type-vocabulary.js` は **`docs/templates/*.md` の `type:` を実行時に読んで値域を組み立てる。**

**値域を検査器や `docs/README.md` へ写さない。** 写すと二重定義になり、片方が古くなる
（[IADR-0166](./IADR-0166_status-vocabulary-and-record-rewrite-boundary.md) 決定 1 と同じ理由）。
**テンプレートに種別を足せば、その値は自動で許される。**

## ★★ 決定 2: **1 枚のテンプレートを 2 つの種別が共用しない**

**これが #675 の本当の欠陥だった。**

| 種別（引数） | 是正前のテンプレート | 是正前の `type` |
| --- | --- | --- |
| `operations`（運用仕様書） | `operations_spec_template.md` | `operations-spec` |
| `runbook`（運用 Runbook） | **同じ** | **同じ** |
| `work`（作業仕様書） | `spec_template.md` | `spec` |
| `how-to`（手順ガイド） | **同じ** | **同じ** |

**`type` を読んでも種別が決まらない。**
**`runbook_template.md`（`type: runbook`）と `how_to_template.md`（`type: how-to`）を新設**して塞いだ。

**実データ側へ寄せる方向である**（手書きの `runbook` / `how-to` のほうが正しい）。
Runbook を `operations-spec` と名乗らせると、**「運用仕様書はリポ単位で 1 つ」という規約と、
Runbook が複数ある実体が矛盾する。**

## ★★ 決定 3: **これは #667 で入れた検査器の穴だった**（自分の直前の PR）

[IADR-0166](./IADR-0166_status-vocabulary-and-record-rewrite-boundary.md) 決定 4 は、`status` の値域から
`runbook` / `how-to` / `how-to-guide` / `tech-note` / `design` を **`type` の値で**除外した。

**ところがこの 5 つは、どれもテンプレートが書かない値だった。** 実データにあるのは手書きの結果であり、
**`/new-spec runbook` で新しい Runbook を作れば `type: operations-spec` になって除外が効かない。**

> **★ #667 の除外は「いま実データがそうなっている」ことに依存しており、仕組みで担保されていなかった。**
> 決定 2 でテンプレートが `runbook` / `how-to` を書くようにしたことで、**除外が仕組みとして成立する。**

**併せて `how-to-guide` を除外一覧から落とした** —— #675 で `how-to` へ寄せたため、
**実在しない値を許し続ける必要がない。**

## 決定 4: **`docs/specs` の 70 件は `spec` へ寄せる**（テンプレートを動かさない）

`spec`（194）・`work-spec`（64）・`work`（6）は同じもの（作業仕様書）を指す。
**テンプレートが書く `spec` へ寄せる。** 逆向きは 194 件の是正になり、決定 1 とも逆行する。

> **`spec` という名前は良くない**（すべての文書が仕様である）。**改名は本 PR の射程外** ——
> 決定 1 で正本をテンプレートと決めた以上、改名はテンプレートを動かす別の判断である。

**`type` の書き換えは [IADR-0166](./IADR-0166_status-vocabulary-and-record-rewrite-boundary.md) 決定 2 の
「語彙の是正」にあたる**（`work-spec` と書いた人も `spec` と書いた人も、同じ作業仕様書を作ったつもりである）。
**`status` と違い `type` には「状態の進行」に相当するものが無い** —— 文書の種別は後から変わらない。

## 決定 5: **`docs/tech` の 3 値は是正せず据え置く**

`docs/tech` は種別表が「リポ単位（原則 1 つ）」と定めるのに**実体が 5 件**あり、
**技術要件書・アーキテクチャ・区分表・実装ガイド・PoC 記録**が同居している。
`tech-requirements` へ一律に寄せると**内容と名前が食い違う。**

**これは `type` の語彙の問題ではなく種別の設計の問題**であり、**据え置き（ラチェット）とする**
（`tech` 2 / `tech-note` 1 / `tech-architecture` 1 / `design` 1）。**増えたら落ちる。**

## ★★ 決定 6: **再発防止の軸を 1 度取り違えた。直したものを捕まえるか確かめた**

**初版の検査器は「2 枚のテンプレートが同じ `type` を書いていないか」を見ていた。**
**是正前の状態を組み立てて当ててみたところ、衝突 0 件で素通りした**（実測）。

**欠陥は「2 枚が同じ値を書く」形ではなく「1 枚を 2 種別が共用する」形**だったからである。
**直したはずのものを捕まえない検査器を書いていた。**

**書き直した**: `.claude/commands/new-spec.md` の対応表を読み、
**種別 → テンプレート → `type` を解決して、2 つの種別が同じ `type` に落ちないことを検査する。**

**是正前の状態へ差し戻して当て直し、2 件の衝突を検出して exit 1 になることを確かめた。**

```console
[check-doc-type-vocabulary] 種別 `how-to` と `work` がどちらも type "spec" に落ちている。…
[check-doc-type-vocabulary] 種別 `operations` と `runbook` がどちらも type "operations-spec" に落ちている。…
exit=1
```

> **「変異試験を書いた」ではなく「変異が当たっているか」を確かめる。**
> #665 でも #667 でも同じ型の取りこぼしをしており、**3 回目である。**

## 結果

- `docs/templates/runbook_template.md` / `how_to_template.md`（新規。決定 2）
- `.claude/commands/new-spec.md`（対応表の追随 ＋ 「種別表は引数の語彙である」の明記）
- **76 件の `type` を是正**（`work-spec` 64 ／ `work` 6 → `spec`、`how-to-guide` 2 → `how-to`、
  `functional` 2 → `functional-spec`、`test` 2 → `test-spec`）
- `scripts/check-doc-type-vocabulary.js`（新規。自己試験 13 件。frontmatter の解析は #667 の検査器と共有）
- `scripts/check-doc-status-vocabulary.js`（除外一覧から `how-to-guide` を削除。決定 3）
- `docs/README.md`（決定 1・2 の明記）

### 門は 3 つあり、別々に変異試験する

**#665 / #667 の教訓 —— 1 つの変異で全部を確かめたつもりにならない。**

| 門 | 発火条件 |
| --- | --- |
| **A** | `docs/templates` から `type` を 1 件も読めない（値域を作れない） |
| **B** | `docs/` を走査して `type` を持つ文書が 0 件（[IADR-0130](./IADR-0130_test-spec-coverage-ratchet.md)） |
| **C** | `new-spec.md` の種別表から 1 行も読めない（表の書式が変わった） |

**門 A を持たないと「値域が空 ＝ 何も許されない」ではなく「走査 0 件で緑」に化ける。**

### フォローアップ

1. **`spec` の改名**（決定 4）—— テンプレートを動かす別の判断。
2. **`docs/tech` の種別設計**（決定 5）—— 据え置きを解消するには種別そのものを決める必要がある。
3. **`type` を持たない文書 7 件** —— `README.md` 等。**値域の問題ではない**が、
   「持つべきなのに持っていない」ものが混じっていないかは未確認である。
