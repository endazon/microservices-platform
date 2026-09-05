---
title: SC-03 に SC-18 への導線を足し、不在を固定していたテストを存在の固定へ置き換える（配置規約の E2E を含む・#1240）
type: spec
status: done
related_ids: [FR-17, FR-18, SC-03, SC-04, SC-18, UC-01, UC-02, UC-07, UC-10, ADR-0033, ADR-0034, ADR-0035, ADR-0066, IADR-0119, IADR-0124, IADR-0300, IADR-0365, IADR-0387]
author: Claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/07_adr/ADR-0033_knowledge-graph-data-model-and-store.md
issue: "#1240"
---

# #1240: SC-03 → SC-18 の導線と、配置規約の E2E

## 起点となる計画書（トレーサビリティ）

- 画面: `SC-03`（文書詳細／プレビュー）・`SC-18`（ナレッジグラフビュー）・`SC-04`（Wiki 閲覧）
- 機能要求: `FR-17`（文書間リンク＝知識グラフ）。承認欄（`FR-18`）は着地済みで本作業の対象外。
- ユースケース: `UC-01` / `UC-02` / `UC-07`（文書詳細への到達）・`UC-10`（グラフの探索）
- 計画 ADR: `ADR-0033`（辺の型・データモデル）・`ADR-0034`（ホップごと ABAC・存在秘匿）・`ADR-0035`
- 計画の確定事項（`05_screens` §SC-03「知識グラフ」・2026-08-02 の利用者裁定）:
  - **バックリンク欄・ローカルグラフは `SC-04` のみに置き、`SC-03` には併置しない。**
  - **`SC-03` に置くのは 2 つだけである**: ①`SC-18` への導線、②AI 提案の承認欄。
- 実装 ADR: `IADR-0119`（着手保留。2026-08-07 に `FR-17` / `FR-18` について解除済み）/
  `IADR-0300`（承認欄）/ `IADR-0124`（ルート）/ 本作業の `IADR-0387`

## 1. 事象（自分で測った。陽性対照つき）

### 1-1. `SC-03` に `/graph` への導線が無い

```console
$ git grep -nE 'to=|href=' -- src/knowledge/frontend/src/features/sc03-document
AiSuggestionPanel.tsx:132:            to="/ai-suggestions"
DocumentDetailPage.tsx:213:            to="/wiki"
DocumentDetailPage.tsx:230:            href={sourceUri}
```

`/graph` は **0 件**である。

**陽性対照 1**（走査が生きている）: 同じ走査が `/ai-suggestions` と `/wiki` を拾っている。
**陽性対照 2**（繰り延べの相手が実在する）:

```console
$ git grep -n "path:" -- src/knowledge/frontend/src/features/sc18-graph/routes
sc18GraphRoute.ts:39:    path: '/graph',
```

**陽性対照 3**（`/graph` へのリンクは本当にリポジトリ全体で 0 件）:

```console
$ git grep -n 'to="/graph"' -- src
（0 件）
```

### 1-2. 繰り延べの発火条件は 2026-08-07 に成立し、判断先が消えた

`DocumentDetailPage.tsx` L49-57 が「発火条件が成立した」と自ら書きながら
「知識グラフビューへの導線は依然として実装しない（`SC-18` の画面と同じ段で足す）」で止まっている。
その `SC-18` は着地済み（`src/knowledge/frontend/src/features/sc18-graph/` が実在する）。
**条件は満たされ、解除を持つ者が居なかった。**

### 1-3. 不在を固定するテストが立っている（＝足すと赤くなる）

`DocumentDetailPage.test.tsx:331` の
`does not render the knowledge-graph link (SC-18 belongs to another screen)` が
`queryByText(/知識グラフ/)` と `queryByRole('link', { name: /グラフ/ })` の 2 本で不在を固定している。

### 1-4. 配置規約は E2E で 1 箇所も固定されていない

```console
$ grep -rniE "backlink|バックリンク|ローカルグラフ" src/platform/frontend/e2e/
（0 件・exit 1）
```

**陽性対照**（不在アサーションはこの E2E 群の常用イディオムである）:

```console
$ grep -rncE "toHaveCount\(0\)|not\.toBeVisible" src/platform/frontend/e2e/
（14 ファイルがヒット。sc03 に 2 件・sc04 に 4 件）
```

Vitest 側（`DocumentDetailPage.test.tsx:581`）だけが不在を固定している。

## 2. 母集合（規則 9・10。誤りの側の文字列で全文書を走査してから挙げた）

走査 1: `git grep -lnE "グラフ(ビュー)?への導線|グラフで見る"`（`src/ai-stock-trading` を除外）

| ファイル | 何が誤りになるか |
| --- | --- |
| `src/knowledge/frontend/src/features/sc03-document/components/DocumentDetailPage.tsx` | 冒頭コメント「導線は依然として実装しない」 |
| `src/knowledge/frontend/src/features/sc03-document/components/DocumentDetailPage.test.tsx` | 不在テスト本体とその前置きコメント |
| `docs/screens/SC-03_document-detail.md` | 冒頭の実装状態注記 (1) / 対応表 #7 / §実装しない要素の理由 / §未決事項 1 |
| `docs/tests/SC-03_document-detail.md` | 受け入れ基準の写像表とテスト一覧の行 10 |

走査 2: `grep -rn "導線" docs/screens/SC-18_knowledge-graph.md`

| ファイル | 何が誤りになるか |
| --- | --- |
| `docs/screens/SC-18_knowledge-graph.md` | 冒頭注記「文書詳細画面からの導線は未実装」/ 図の点線 / 対応表「しない」/ §未決事項 |

走査 3: `git grep -l "IADR-0119"` → `.ai-context/adr/` と `.ai-context/specs/` に 30 件。

**除外理由**: `.ai-context/specs/` と `.ai-context/superpowers/` は**凍結記録**であり本文を書き換えない
（`traceability.repo.md` §Superseded / Deprecated な ADR を引用するときの書式）。
`.ai-context/adr/IADR-0119` 本体は「2026-08-07 に `FR-17` / `FR-18` の保留を解除した」と既に書いており、
**本作業でその決定は変わらない**（解除済みの保留を消費するだけ）ので追記の対象ではない。

走査 4（規則 10 —— 是正後に新たに誤りになる自分の記述）:
`SC-04` 側の「バックリンク欄・ローカルグラフは未実装」は**本作業で変わらない**。
本作業が固定するのは **`SC-03` に無いこと**だけであり、`SC-04` に在ることは固定しない（§4-3）。

## 3. 判断

### 判断 1: 導線のラベルは「ナレッジグラフで見る」にする

モック（`hi-fi/sc-03.html` L422）は `◉ 知識グラフで見る（SC-18）` と描くが、
`SC-18` のルート定義（`sc18GraphRoute.ts` L71-73）が既に
「モックの crumb は『知識グラフ』だが計画の画面名・左ナビは『ナレッジグラフ』であり、
計画は同じものに 2 つの名前があることを名指しで避けている（§用語）ので、シェルの中で 1 つの名前に揃える」
と決めて左ナビ・パンくずを `ナレッジグラフ` にしている。**その先例に従う**（新しい判断ではない）。
モックの `（SC-18）` は計画 ID の露出なので画面には出さない。

### 判断 2: `root` / `hops` / `by` を明示して渡す

`SC-18` は `root` 未指定だと案内文を出す仕様なので、**起点を渡さない導線は作らない**。

🔴 **当初ここには「`search={{ root: doc.id }}` だけを渡す（既定値の情報源を 2 つにしない）」と書いていたが、
それは誤りである。** `sc18GraphRoute.ts` の `GraphSearch` は `root` / `hops` / `by` の 3 つとも必須であり
（任意なのは `types` だけ）、**`root` だけを渡す形は型が通らない**。
既定値は `validateSearch` の**内側**にあり、`ADR-0066` 決定 1（feature どうしを import しない）が
引いて来ることを禁じている。したがって `search={{ root: doc.id, hops: 2, by: 'distance' }}` を渡す ——
**これは既定値の複写ではなく明示の要求であり**、渡した値は `SC-18` 側の `validateSearch` が値域で丸めるので、
こちらが古くなっても壊れ方は「別の深さで開く」に留まる（論拠の正本は `IADR-0387` 決定 1）。

### 判断 3: 「不在の固定」を「存在の固定」へ書き換える（消さない）

受け入れ基準 2 のとおり、不在テストは**削除せず反転させる**。消すと、次に導線が失われても緑のままになる。

### 判断 4: `SC-04` 側は「在ること」を固定しない

`docs/screens/SC-04_wiki-access.md` §未決事項 2 が「バックリンク欄・ローカルグラフの実現方式は
計画側で未確定」としている。**未確定のものを E2E で固定すると、計画が決めたときに実装ではなく
テストが先に決めたことになる。** `sc04-wiki.smoke.spec.ts` へ足すのは
**「`SC-04` の現状（バックリンク欄をまだ持たない）を、`SC-03` の不在と取り違えない」ための注記と、
現状を変えない陰性対照 1 本**だけである。

## 4. 実装

### 4-1. `DocumentDetailPage.tsx`

`SourceLinks` の並び（モック L422 と同じ行）へ `｜ ◉ ナレッジグラフで見る` を足す。
`Link to="/graph" search={{ root: doc.id, hops: 2, by: 'distance' }}`（判断 2）。冒頭コメントの「実装しない」節を、
**実装した事実 ＋ 併置しないものの明示**へ書き換える。

### 4-2. `DocumentDetailPage.test.tsx`

- `does not render the knowledge-graph link …` → `links to SC-18 with this document as the graph root`
  に置き換え、`href` が `/graph?root=<id>&hops=2&by=distance` であることまで見る（**リンクが在るだけでは
  「起点を渡す」を満たさない**）。
- 陰性対照（`ADR-0034` の完全秘匿）: **404 の中立表示のときは導線を描かない**
  （文書が在るかどうかを導線の有無で漏らさない）。
- バックリンク欄の不在テスト（L581）は**そのまま残す**。新しい導線の語（`ナレッジグラフ`）が
  `ローカルグラフ` の正規表現に当たらないことを確認済み。

### 4-3. E2E

- `e2e/sc03-document.smoke.spec.ts`: (a) 導線が実在し `href` が `/graph?root=<id>&hops=2&by=distance` であること
  (b) バックリンク欄・ローカルグラフが `SC-03` に無いこと。
- `e2e/sc04-wiki.smoke.spec.ts`: 非対称の理由をコメントで明示し、**`SC-04` に在ることは固定しない**。

### 4-4. 文書

`docs/screens/SC-03_document-detail.md` / `docs/tests/SC-03_document-detail.md` /
`docs/screens/SC-18_knowledge-graph.md` を実態へ合わせる（計画 ID・IADR・仕様書名は trace ブロックへ）。

## 5. 受け入れ基準

1. `SC-03` に `SC-18` への導線が描画され、起点文書 ID を引き渡す。
2. 不在テストが**存在の固定へ置き換わっている**（不在のまま残っていない）。
3. `e2e/sc03-document.smoke.spec.ts` が (a) 導線の実在 (b) `SC-03` にバックリンク欄が無いこと を固定する。
4. `e2e/sc04-wiki.smoke.spec.ts` は `SC-04` 側の現状を変えず、非対称の理由が spec とテストに書いてある。
5. 文書 3 本が実態に合っている。
6. `node scripts/check-route-manifest.js` / `pnpm run lint` / `typecheck` / `test` / `format:check` が緑。

## 6. 変異試験

| # | 変異 | 落ちるべきテスト |
| --- | --- | --- |
| M1 | `search` の `root` を `''`（空文字）にする | 単体「起点を渡す」・E2E の `href` |
| M2 | 導線を削除する | 単体「存在の固定」・E2E |
| M3 | 404 のときも導線を描く | 単体の陰性対照 |
