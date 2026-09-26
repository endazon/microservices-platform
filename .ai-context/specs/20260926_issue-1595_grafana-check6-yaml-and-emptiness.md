---
title: "Grafana ルールの検査（検査 6）の残る取りこぼしと、YAML 読み取りの複数行の expr を直す（#1595）"
type: spec
status: done
related_ids: [NFR-21, ADR-0006, IADR-0165]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md NFR-21（障害検出 5 分以内）
related_specs: [20260926_issue-1588_grafana-rule-verify-and-workflow-read-scopes.md, 20260926_1577_grafana-filter-evaluator-never-fires.md]
issue: "#1595"
---

# 作業仕様書 — Grafana ルールの検査（検査 6）の残る取りこぼしと、YAML 読み取りの複数行の expr を直す（#1595）

## 起点

- issue: #1595（#1592〔#1588〕の監査で出た、ブロックしない指摘）。
- 起点 ID: **NFR-21**（Grafana 暫定アラートの検査 6。永久に発火しないルールは障害検出を黙って失わせる）。
- 計画 ADR: ADR-0006（アラートは Alertmanager。Grafana は暫定。改めない）。実装 IADR: IADR-0165（Grafana 暫定アラート）。
- **新しい IADR は起こさない**（検査器の読み方の拡張であり新たな設計判断ではない）。IADR-0165 へ日付つき追記を 1 つ足す。

## 着手時の現況（`origin/develop` = 8a51ec30）

- `readExprs` は行の正規表現で `expr:` を拾い、plain の値は 1 行目だけを読む。`expr: up` の次の行に `== 0` があると `up` として読み、`gt 0` と組んでも通る（偽陰性）。
  評価器も `evaluator: { type: …, params: [...] }` の順の 1 行のフローしか読まず、行末コメントのある `refId` / `type` / `title` も読めない。
- 値の範囲だけを見て、空集合（決して値を返さない式）を見ていない。`up - up` は任意の値として読む。
- `or on (…)` の右辺の修飾を剥がさない／関数呼び出しへのサブクエリを読まない／`@` が `offset` より前だと読まない／`:offset` で終わる名前の後の `-` を単項として読む。
- 単項の符号を `^` より強く結ぶ（`-2 ^ 2` を 4 と読む）。「任意の値」が開区間で ±Inf を含まない（`x == +Inf` を空と誤判定する）。
- `UNVERIFIABLE_ALLOWLIST` は理由が空でも黙らせる。`expression: $A` は「refId が無い」という紛らわしい文で報告される。
- 実データ 20 件は 7 つの形に収まる（#1588 の作業仕様書 §1 の表と同じ）。

## 設計

### 1. YAML は木として読む（依存を入れない）

- scripts/ は依存を入れずに node だけで走る（`ci.yml` の scripts ジョブは install しない。YAML を読む他の検査器も自前で読む）。**リポジトリに YAML ライブラリの依存は無い**（`package.json` はルートに無く、`src/node_modules` にも頼れない）ので、**本ファイルの書式が使う YAML の部分集合の読み手**を検査器に持つ。
- 読む: ブロックの写像・列（`- key: v` の詰めた形・キーと同じ字下げの列）／フローの写像・列（複数行・入れ子）／plain（複数行の続き・行末コメント・core schema の型）／一重・二重引用符（複数行の折り畳み・エスケープ）／`|` / `>`（字下げ・chomping の指示子）／先頭の `---`。
- 読まない（**写しごとに違反**）: アンカー・エイリアス・タグ・複合キー・複数文書・ディレクティブ・タブの字下げ・重複キー・plain の値の中の「: 」・コメントの後の続き。
- ルールは `groups[].rules[]`、ノードは `data[]` の `refId` / `datasourceUid` と `model` の `type` / `expression` / `expr` / `conditions[].evaluator` から取る（キーの順に依らない）。`params` は数だけを受理する（Grafana は `[]float64` として読む）。
- 検査 1〜3 の行の読み取り（`title` / `alert` / `datasourceUid` / `uid`）にも行末コメントを許す（検査 6 と食い違わせない）。

### 2. 空集合 —— 健全に認識できる形だけ（射程を検査器の「6.」の節に書いた）

- (a) **空の伝播**: `and` のどちらかの辺・算術のベクタの辺・集約 / 関数の引数・比較の辺が空なら空。`or` は和。`unless` は左辺が空なら空、右辺が空なら左辺。
- (b) **同じ式の自己矛盾**: `E op1 c1 and E op2 c2` → 両方の絞り込みの交わり（**E の系列が名前を除いたラベルで一意に決まり、照合の修飾が無いときだけ**。名前を持つ選択子・`vector(定数)`・系列を作り直す集約〔`by (__name__)` を除く〕・`histogram_quantile`・名前を持つ選択子に掛けた範囲の関数）。
  `E unless E`・`E op c unless E` → 空、`E unless E op c` → E の値 ∩ 絞り込みの補集合（系列は自分自身と必ず照合するので修飾の有無によらず健全）。
  同じ式どうしの差・商（`E - E` → {0}・`E / E` → {1}）も同じ一意性の条件で定数に畳む。
- (c) **ラベルの矛盾**: 照合に使うラベル（`on (…)` の中だけ／`ignoring (…)` の外／既定は名前以外すべて）で、両辺の確定した値が食い違えば `and`・ベクタどうしの比較・算術は空（`unless` は何も除かない）。
  値が確定するのは選択子の等号の照合子と、ラベルを持たないことが確定した辺（`vector(…)`・`sum(…)`・`sum by (l)(…)` の外・`absent(…)` の非等号）。ベクタどうしの算術・比較と `or` の後はラベルを「不明」とする。
- **報告する（検証できない）**: (b) と同型だが健全に判定できない形 —— 名前の無い選択子・照合の修飾つき・`by (__name__)` の自己矛盾、NaN の標本だけが残り得る `unless`（`!=` 以外の絞り込みは NaN を通さない）。`and` / `unless` / `or` の読めない右辺も報告する（#1588 までは `and` の右辺を読んでいなかった）。
- **完全は目指さない**: 上に挙げない `and` / `unless` は「値を返し得る」とする（異なる式どうしは一般にデータ次第で照合し得る）。見逃し側であり偽陽性は出さない。

### 3. 端のケース

- 「任意の値」を **±Inf を含む閉区間**にする（絞り込み・評価器の区間も ±Inf 側の端を含む。Go の比較で `+Inf > 0` は真）。NaN は集合に入れない（`ne` の評価器で NaN が真になる端は近似として受容し、IADR-0165 の追記に書いた）。
- 単項の符号は `^` より弱い（PromQL の `unary_expr` は `%prec MUL`。`-2 ^ 2` は -4、`2 ^ -1` の符号は右辺の中）。
- `or on (…)` / `or ignoring (…)` の右辺の修飾を剥がす。`group_*` は `or` / `and` / `unless` に付けられないので報告する。
- サブクエリ `<式>[範囲:刻み]`（関数呼び出し・括弧にも付く。後ろの `offset` / `@` を含む）は中の式の値を保つ。
- 選択子の `offset` と `@` はどちらの順でも読む。語の境界に `:` を含める（`job:up:bool`・`foo:offset` を修飾子として読まない。`== bool:m` はベクタどうしの比較）。
- 文字列リテラルは中身ごとに番号へ置き換える（#1588 までは `""` に潰していた。ラベルの照合と同じ式の判定に中身が要る）。コメントと文字列は 1 回の走査で見分ける。
- `expression: $A` は**違反**（Grafana の threshold は `$` を剥がさない。grafana/grafana@92b769af の `pkg/expr/threshold.go`〔`referenceVar := cmdConfig.Expression`〕と `commands.go`〔reduce / resample だけ `strings.TrimPrefix(…, "$")`〕を `gh api` で読んだ）。読み替えて通すと Grafana が評価できない式を緑にする。
- `UNVERIFIABLE_ALLOWLIST` は理由が空・空白だけ・文字列でない項目を違反にし、その項目では黙らせない。

## 母集合（誤りになる記述の走査。パス除外: `src/ai-stock-trading` / `CHANGELOG.md`。拡張子で絞らない）

`git grep -n "check-grafana-alerting\|UNVERIFIABLE_ALLOWLIST\|readExprs\|grafanaRuleConditions"` を検査器と `scripts.repo.test.js` を除いて `origin/develop`（8a51ec30。本作業の記録を書く前）に対して引いた（86 行。本仕様書と IADR-0165 の追記は含まない）。

| 軸 | 拾ったもの → 扱い |
| --- | --- |
| 1 検査器の API の利用者 | `scripts/scripts.repo.test.js` だけ → 名指しの変異ケースに 16 件、実データの変異試験を 1 件追加。`grafanaRuleConditions` の戻り値の形は互換（`title` / `condition` / `nodes` / `exprs` / `evaluators`）。YAML を読めなければ `YamlSubsetError` を投げる（`filterEvaluatorIssues` が違反へ変える） |
| 2 検査 6 の射程の言明 | `docs/operations/operations.md`（範囲の列挙）→ 「YAML として読み、読めなければ違反・決して値を返さない式も止める」を追記。IADR-0165 の #1577 追記（「比較の上の算術は値を縛らないものとして読む」）→ #1588 で既に古い。本文は書き換えず #1595 の追記で明記。`slo-alerts.yaml` / `grafana.yaml` 冒頭の「機械で確かめたのは…まで」は誤りではない（範囲の下限の列挙）ので改稿しない（両写しの同内容の検査があり、コメントだけの改稿は #1588 と同じく見送る）。`docs/observability/rag-first-token-latency.md`（1 対 1 の突合）は正しいまま |
| 3 行の正規表現で読むという記述 | `.ai-context/specs/` の確定済み 2 件（#1577 / #1588）→ 凍結記録なので書き換えない |
| 4 件数（導出値） | 自己試験 38 → 54 件。件数を書く live な文書は無い（`scripts/README.md` に本検査器の行は無い）。IADR-0165 本文の「自己試験 10 件」は #665 時点の記録 |

## 受け入れ基準

- [x] 複数行に続く plain の `expr:` を続きまで読み、`gt 0` との組み合わせを検出する（実データの変異試験でも写しの両方）
- [x] `up == 0 and up == 1`・`up unless up`・ラベルの矛盾を「決して値を返さない」として検出し、健全に判定できない同型は「検証できない」と報告する
- [x] `or on (…)`・単項の符号と `^`・±Inf・サブクエリ・`@` と `offset`・`:offset` / `:bool` の名前・行末コメント・`{ params, type }`・`expression: $A`・許可リストの空の理由 —— それぞれに自己試験がある
- [x] 実データの 20 件がすべて通り、許可リストは空（`checked` がルール数と一致）
- [x] 検査器の修正を 1 つずつ戻す変異 23 通りが、すべて自己試験で落ちる

## 検証

- `node scripts/check-grafana-alerting.js --self-test` → 54 件通過
- `node scripts/check-grafana-alerting.js` → OK（Prometheus 20 / Grafana 20・組み合わせ 20 件・許可リスト 0 件）
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` → 全件 pass
- **変異試験（検査器のソース）**: 修正を 1 つずつ戻した写し（スクラッチ）で `--self-test` を走らせた。23 通りすべて exit 1
  （M01 plain の続きを読まない＝既存の不具合／M02 行末コメント／M03 フローの型／M04 `$A`／M05・M06 自己矛盾の絞り込み／M07 ラベルの矛盾／M08 空の伝播／M09 `or on`／M10 単項と `^`／M11 ±Inf／M12 サブクエリ／M13 `@` と `offset` の順／M14・M15 `:` を含む語境界／M16 許可リストの空の理由／M17 `E - E`／M18 NaN だけが残る `unless`／M19 一意性の条件／M20 文字列を 1 つに潰す／M21 読めない YAML を黙って飛ばす／M22 空集合を報告しない／M23 `and` の右辺を読まない）。
- **旧検査器との対照**（`origin/develop` の検査器に実データの変異を当てた）: 複数行の plain の `== 1`・`x unless x`・`x == 0 and x == 1`・`up - up` × `gt 0` は旧で違反 0 件 → 新で写しの両方に違反。
  `x == +Inf` × `gt 0` は旧で「永久に発火しない」の誤検出 → 新で違反 0 件。`x == 0 or on() vector(0)` は旧で「読めない形」→ 新で「永久に発火しない」。

## 未検証

- Grafana が provisioning を受理するか（IADR-0165 決定 1 のまま）。稼働クラスタには当たっていない（LIVE なし）。
