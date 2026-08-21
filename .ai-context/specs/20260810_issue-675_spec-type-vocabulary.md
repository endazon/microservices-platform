---
title: 作業仕様書 — 仕様書の `type` をテンプレートの値へ揃え、種別が一意に決まるようにする（#675）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0130
  - IADR-0166
author: claude
created: 2026-08-10
updated: 2026-08-10
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
---

# 作業仕様書: 仕様書 `type` の値域（#675）

## 起点

- **NFR**（文書統制）。実装 ADR: **[IADR-0166](../adr/IADR-0166_status-vocabulary-and-record-rewrite-boundary.md)** 決定 5 が本 issue へ送った
- 起点 issue: **#675**（**自分で起票した**。出所は #667 / PR #676）

## 母集合（自分で引き直した）

### 軸 1: issue 番号で引く

```console
$ git ls-files -z ':!planning' ':!src/ai-stock-trading' | xargs -0 grep -ln '#675'
docs/adr/IADR-0166_status-vocabulary-and-record-rewrite-boundary.md
docs/specs/20260810_issue-667_spec-status-vocabulary.md
```

**2 件。いずれも #667 側から「#675 へ送った」という申し送り**であり、実装指示は無い。

### ★ 軸 2: **issue の「三つ巴」という前提が誤っていた**

**#675 はこう書いている（自分で書いた）:**

> **どこへ寄せるかが三つ巴** —— `spec_template.md` は `type: spec` を書き、`CLAUDE.md` の種別表は
> `work` と呼び、実データの多数派は `work-spec` である。

**誤りである。** `.claude/commands/new-spec.md` を読むと、**種別表は `/new-spec` の「引数」の語彙**であって
`type:` の値ではない。引数・テンプレート・出力先の 3 列の対応表であり、`type` の列は存在しない。
**`type` の値を決めているのはテンプレートだけ**である。

**19 種別すべてで、引数と `type` は機械的に対応していた**（実測）。

| 対応 | 例 | 件数 |
| --- | --- | ---: |
| **`<引数>-spec`** | `functional` → `functional-spec` / `api` → `api-spec` | 15 |
| **例外** | `work` → `spec` ／ `adr` → `impl-adr` ／ `runbook` → `operations-spec` ／ `how-to` → `spec` | 4 |

**つまり「引数の語彙」と「`type` の語彙」は別物で、規則的に対応している。**
**三つ巴ではなく、二層である。** #675 は 2 つの層を 1 つの軸に潰して数えていた。

### ★ 軸 3: **本当の欠陥は「別の種別が同じ `type` になる」2 組**

上の例外 4 つのうち **2 つは衝突している**。

| 引数（種別） | テンプレート | 書かれる `type` | 衝突 |
| --- | --- | --- | --- |
| `operations`（運用仕様書） | `operations_spec_template.md` | `operations-spec` | **同じ** |
| `runbook`（運用 Runbook） | `operations_spec_template.md` | `operations-spec` | **同じ** |
| `work`（作業仕様書） | `spec_template.md` | `spec` | **同じ** |
| `how-to`（手順ガイド） | `spec_template.md` | `spec` | **同じ** |

**`type` を読んでも「運用仕様書か Runbook か」「作業仕様書か手順ガイドか」が区別できない。**

### ★★ 軸 4: **これは #667 で入れた検査器の穴である**（自分の直前の PR）

**[IADR-0166](../adr/IADR-0166_status-vocabulary-and-record-rewrite-boundary.md) 決定 4** は
`status` の値域から `runbook` / `how-to` / `how-to-guide` / `tech-note` / `design` を**対象外**にした。
その除外は **`type` の値で判定している**（`scripts/check-doc-status-vocabulary.js` の `EXEMPT_TYPES`）。

**ところがこの 5 つの値は、どれもテンプレートが書かない。** 実データにあるのは**手書きの結果**である。

```console
$ # docs/operations の Runbook 2 件
docs/operations/llm-cost-monthly-review-runbook.md   type: runbook   ← 手書き
docs/operations/local-sso-recovery-runbook.md        type: runbook   ← 手書き
$ # テンプレート経由で作ると
/new-spec runbook … → operations_spec_template.md → type: operations-spec
```

**したがって `/new-spec runbook` で新しい Runbook を作ると `type: operations-spec` になり、
`EXEMPT_TYPES` に当たらず検査対象に入る。** 手順書に `status: draft` / `in-progress` /
`completed` のどれを書けというのかが決まっていないため、**書き手が困る形で CI が止まる。**

> **★ #667 の除外は「いま実データがそうなっている」ことに依存していた。**
> **仕組みとして担保されていない。** 本 PR はここを塞ぐ。

### 軸 5: **テンプレートの値からの逸脱を全数で数えた**

**出力先ディレクトリごとに「テンプレートが書く `type`」を引き、実データと突合した。**

| 出力先 | テンプレートの `type` | 実データ | 件数 |
| --- | --- | --- | ---: |
| `docs/specs` | `spec` | **`work-spec`** | **64** |
| `docs/specs` | `spec` | **`work`** | **6** |
| `docs/functional` | `functional-spec` | `functional` | 2 |
| `docs/how-to` | `spec` | `how-to-guide` | 2 |
| `docs/operations` | `operations-spec` | `runbook` | 2 |
| `docs/tech` | `tech-requirements` | `tech` | 2 |
| `docs/tests` | `test-spec` | `test` | 2 |
| `docs/how-to` | `spec` | `how-to` | 1 |
| `docs/tech` | `tech-requirements` | `tech-note` | 1 |
| `docs/tech` | `tech-requirements` | `tech-architecture` | 1 |
| **計** | | | **83** |

**`docs/specs` の 70 件（`work-spec` 64 ＋ `work` 6）が大半である。**

### 軸 6: **`docs/tech` は「リポ単位（原則 1 つ）」と言いながら 5 件ある**

`docs/tech` は `tech-requirements` 1 ＋ `tech` 2 ＋ `tech-note` 1 ＋ `tech-architecture` 1 = **5 件**。
種別表は**リポ単位（原則 1 つ）**と定めているが、実体は
**技術要件書・アーキテクチャ・区分表・実装ガイド・PoC 記録**の 5 種類である。

**これは `type` の語彙の問題ではなく、種別の設計の問題**である。**射程外**にする（後述）。

## 判断

### 判断 1: **正本はテンプレートである**（[IADR-0166](../adr/IADR-0166_status-vocabulary-and-record-rewrite-boundary.md) 決定 5 の踏襲）

`type` の値を書いているのはテンプレートだけである。**種別表（`/new-spec` の引数）を `type` の正本と
読むのをやめ、その旨を明記する** —— 混同したのは #675 を書いた自分である。

### ★ 判断 2: **同じ `type` を 2 つの種別が共有しない**（軸 3 の衝突を解く）

**テンプレートを 2 枚新設する。**

| 種別 | 現状 | 変更後 |
| --- | --- | --- |
| `runbook` | `operations_spec_template.md`（`type: operations-spec`） | **`runbook_template.md`**（`type: runbook`） |
| `how-to` | `spec_template.md`（`type: spec`） | **`how_to_template.md`**（`type: how-to`） |

**実データ側へ寄せる方向である。** 理由:

- **手書きの値（`runbook` / `how-to`）のほうが正しい。** Runbook を `operations-spec` と名乗らせると、
  「運用仕様書はリポ単位で 1 つ」という規約と矛盾する（Runbook は複数あってよい。`docs/README.md`）
- **#667 の除外がテンプレート経由でも効くようになる**（軸 4 の穴が塞がる）
- **変更はテンプレート 2 枚 ＋ `how-to-guide` 2 件の是正**で済む

### 判断 3: **`docs/specs` の 70 件は `spec` へ寄せる**（テンプレートを動かさない）

`spec`（194）・`work-spec`（64）・`work`（6）は**同じもの（作業仕様書）を指している**。
**テンプレートが書く `spec` へ寄せる**（70 件の是正）。逆向き（テンプレートを `work-spec` にする）は
**194 件の是正**になり、判断 1（正本はテンプレート）とも逆行する。

> **`spec` という名前は良くない**（すべての文書が仕様である）。**しかし改名は本 PR の射程ではない** ——
> 判断 1 で正本をテンプレートと決めた以上、**名前を変えるならテンプレートを変える別の判断が要る。**
> **射程外**として残す。

### 判断 4: **`type` の書き換えは [IADR-0166](../adr/IADR-0166_status-vocabulary-and-record-rewrite-boundary.md) 決定 2 の線引きに当てはまる**

**`type` は「その文書が何であるか」であり、書き手の当時の判断ではない。**
`work-spec` と書いた人も `spec` と書いた人も、**同じ「作業仕様書」を作ったつもり**である。
**したがって語彙の是正にあたり、書き換えてよい**（`status` と同じ結論）。

**ただし `status` と違い、`type` には「状態の進行」に相当するものが無い** ——
文書の種別は後から変わらない。**線引きの片側だけが効く。**

### 判断 5: **`docs/tech` の 3 値（`tech` / `tech-note` / `tech-architecture`）は是正しない**

**軸 6 のとおり、これは種別の設計の問題である。** `tech-requirements` へ一律に寄せると
**アーキテクチャ文書と PoC 記録が「技術要件書」を名乗る**ことになり、**内容と名前が食い違う。**

**据え置き（ラチェット）とし、別 issue で種別の設計ごと決める。**
**据え置きは件数を固定し、増えたら落ちる。**

### 判断 6: **検査器は別ファイルにし、frontmatter の解析だけを共有する**

**初版はこう書いていた**: 「#667 の `check-doc-status-vocabulary.js` へ足し、
`check-doc-frontmatter-vocabulary.js` へ改名する（走査を 2 度しない）」。**書き直した。**

**改名は割に合わない。** その検査器は**本 PR の直前（PR #676）にマージしたばかり**であり、
`docs/README.md`・[IADR-0166](../adr/IADR-0166_status-vocabulary-and-record-rewrite-boundary.md)・
`scripts/scripts.repo.test.js` が名前で参照している。**10 分前に入れたファイルを改名すると、
履歴が「入れてすぐ改名した」という読みにくい形になる**うえ、参照の追随漏れの危険が増える。

**走査を 2 度することの実費は無い**（`docs/` は 533 枚の Markdown で、1 回の走査は数十 ms）。
**「走査を 2 度しない」は、実測せずに書いた最適化の思い込みだった。**

**したがって `scripts/check-doc-type-vocabulary.js` を新設し、
frontmatter の切り出しだけを `check-doc-status-vocabulary.js` から `require` して共有する**
（**同じ解析器を使うことは明示的に担保される**）。門は #667 と同じ 2 つ（走査 0 件・ラチェット）を持たせる。

## 実装

1. **テンプレートを 2 枚新設**（判断 2）: `runbook_template.md`（`type: runbook`）／
   `how_to_template.md`（`type: how-to`）。`.claude/commands/new-spec.md` の対応表を追随させる
2. **76 件を是正**（判断 3・2）: `work-spec` 64 ＋ `work` 6 → `spec` ／ `how-to-guide` 2 → `how-to` ／
   `functional` 2 → `functional-spec` ／ `test` 2 → `test-spec`
   （**初版は「73 件」と書いていた**。`test` → `test-spec` の 2 件を数え落としていた。**実測で確定させた**）
3. **`docs/README.md`** へ「種別表は `/new-spec` の引数の語彙であり `type` の値ではない」を明記
4. **`scripts/check-doc-type-vocabulary.js` を新設**する（判断 6）。frontmatter の切り出しは #667 の検査器から `require` する

## テスト（受け入れ基準の写像）

| # | 受け入れ基準（#675） | 確かめ方 |
| --- | --- | --- |
| 1 | 作業仕様書を先に作る | 本書 |
| 2 | 母集合を frontmatter だけで数え直す | §軸 5（逸脱 83 件 ＝ 是正 76 ＋ 据え置き 7） |
| 3 | 正本を 1 つに決める | 判断 1（テンプレート）。**issue の「三つ巴」は誤り**（軸 2） |
| 4 | 過去の記録を書き換えてよいかを IADR-0166 決定 2 へ当てはめる | 判断 4 |
| 5 | テンプレート・`/new-spec` を追随させる | 実装 1・3 |
| 6 | 値域を機械検査する | 実装 4（`check-doc-type-vocabulary.js` を新設。解析器は #667 と共有） |

## 射程外

- **`spec` という名前の是非** —— 判断 3。改名はテンプレートを動かす別の判断。
- **`docs/tech` の種別設計** —— 軸 6・判断 5。**別 issue。**
- **`status` の値域** —— #667 / [IADR-0166](../adr/IADR-0166_status-vocabulary-and-record-rewrite-boundary.md) で確定済み。
- **ADR の `type: impl-adr`** —— ゆれていない。
