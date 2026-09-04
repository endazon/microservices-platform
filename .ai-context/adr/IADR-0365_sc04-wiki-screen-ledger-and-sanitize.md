---
title: IADR-0365 SC-04 は権限内 Wiki 台帳を導線の根拠にし、Wiki.js の描画結果を sanitize して SPA の中に描く
type: impl-adr
status: Accepted
related_ids:
  - FR-05
  - FR-13
  - UC-07
  - SC-01
  - SC-03
  - SC-04
  - ADR-0011
  - ADR-0031
  - ADR-0066
  - ADR-0073
  - IADR-0009
  - IADR-0020
  - IADR-0032
  - IADR-0124
  - IADR-0134
  - IADR-0135
  - IADR-0262
  - IADR-0308
  - IADR-0335
  - IADR-0337
  - IADR-0355
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0073_wikijs-ui-not-exposed-sc04-via-gateway.md 決定 1・2・4・5・6
  - planning:projects/microservices-platform/07_adr/ADR-0011_wiki-engine.md（分界。2026-08-15 追記）
  - planning:projects/microservices-platform/03_usecases/01_usecases.md UC-07
  - planning:projects/microservices-platform/05_screens/01_screens.md SC-01 / SC-03 / SC-04
---

# IADR-0365: SC-04 の画面（出典種別の判定・ページツリーの取得単位・本文の sanitize・URL の持ち方）（#1200）

> 🔴 **番号は暫定である。** 起草時点の `develop`（`d06cf387`）の最大は `IADR-0360` だが、
> 進行中の並行 PR が 0361〜0366 を採り得るため 0367 を仮置きした。**マージ直前に実際の空き番号へ
> 付け直し**、ファイル名・本文の自称番号・索引（`.ai-context/adr/README.md`）・作業仕様書・
> コード内コメント（`sc04-wiki/**` / `lib/wiki-pages/**` / `sc01-search/**` / `sc03-document/**` /
> `sc04-wiki.smoke.spec.ts`）・`docs/` の trace ブロック・`deploy/` のコメントを追随させること
> （`scripts/check-adr-numbering.js` は昇順・欠番なしを fail で見る）。

- 状態: Accepted
- 日付: 2026-09-03
- 決定者: claude（実装）

## コンテキストと課題

計画 `ADR-0073`（Accepted / 2026-09-03）決定 2 が **SC-04 §ルート の「Wiki.js 別ホスト・別配信」を撤回**し、
SC-04 を基盤 SPA のルートとして**ページツリー・本文・検索結果を BFF 経由で取得して SPA が描く**と確定させた。
口は #1199（[[IADR-0355]]）が開き、本作業（#1200）が画面を担う。

従前の画面（`WikiAccessPage`）は **`wikiBaseUrl` への外部リンク 1 本**であり、冒頭コメントは
「到達はゲートウェイ（ABAC）経由に限定される（IADR-0020）」と述べていたが、**リンク先の Wiki.js 本体 UI は
前段（WikiService）を通っていなかった**（ADR-0073 §実測 1）。さらに **SC-01 の出典種別（📖 / 📄）と
SC-03 の「Wiki で閲覧」が `WIKI_BASE_URL` の接頭辞判定に依存**しており、決定 1（stg/prod では
`WIKI_BASE_URL` を設定しない）に従うと**本番で一度も真にならない**（issue 実測 4）。

画面を作り直すにあたり、次の 4 点は書かないと次の担当者が逆へ倒しうる。

1. 出典種別（SC-01）と「Wiki で閲覧」（SC-03）の**判定の根拠**を何に置くか。
2. ページツリーを**何回・どの単位で**取るか（台帳に階層は無い）。
3. Wiki.js が描画した HTML を**どう描くか**（そのまま `innerHTML` か、再レンダリングか、sanitize か）。
4. 「どのページを開いているか」を**どこに持つか**（URL か、クライアントストアか）。

## 決定

### 決定 1: 出典種別と「Wiki で閲覧」の根拠は**権限内の Wiki 台帳**（`GET /bff/wiki/pages`）

`src/knowledge/frontend/src/lib/wiki-pages/useWikiPageIndex.ts` が一覧を `select` で**文書 ID の集合**へ畳み、
`citationKind(documentId, wikiDocumentIds)` は集合に含まれれば `wiki`、**未取得・取得失敗（`undefined`）なら
`document`** とする。`sourceUri` と実行時 config は**見ない**。

- 一覧は後段 `WikiService` が ABAC（deny-by-default の `AbacPageFilter`）を通した**権限内のメタデータだけ**を返し
  （BFF は透過。[[IADR-0355]] 決定 5）、**「載っている ＝ 利用者が SC-04 で開ける」**が成り立つ。
- 🔴 **by-doc（`/bff/wiki/pages/by-doc/{id}`）を存在判定に使わない。** 本文（HTML）ごと返る面であり、SC-01 では
  出典 N 件ぶんの往復になる。一覧は 1 回で、SC-04 のページツリーと**同じ生成キー**（`['/bff/wiki/pages']`）なので
  キャッシュを共有する。
- **未取得・取得失敗を `wiki` に倒さない。** 到達できない導線へ送るより、文書詳細（常に辿れる）へ送る。
  従前の `wikiBaseUrl` 未設定時と同じ倒し方である。
- **置き場所は `lib/`**（feature 跨ぎの共有。ADR-0066 決定 1 / [[IADR-0308]] 決定 6 の `scope-filter` と同じ判断）。
  feature 同士は互いを import しない（[[IADR-0262]] 決定 4）。
- 台帳は**出典が現れてから**引く（SC-01 で問う前に Wiki の口を叩かない。`enabled` は出典の有無）。

### 決定 2: ページツリーは `GET /bff/wiki/pages` **1 回**、題名順の**平坦な一覧**として描く

台帳（`WikiPage`）の `wikiPath` は `doc/<documentId>` の**平坦**な正準パスであり（`WikiPage.PathFor`）、
後段は題名順で返す（`ListWikiPagesEndpoint`: `OrderBy(p => p.Title)`）。**階層は台帳に無い**ので、
ツリーは `nav aria-label="ページツリー"` の題名順の一覧として描く。**階層を SPA 側で捏造しない**
（モックの `経理 / 経費精算規程` の中間段に当たる情報源が無い。パンくずの葉だけを `useBreadcrumbLeaf` で渡す）。

### 決定 3: 本文は **Wiki.js が描画した HTML を DOMPurify で sanitize** してから描く

`WikiPageView.content` は Wiki.js が Markdown から描画した HTML である（ゲートウェイがプロキシ。ADR-0073 決定 2 の逐語
「本文の描画そのものは引き続き Wiki.js が行う」）。SPA は **Markdown を再レンダリングしない**。ただし**そのまま
`innerHTML` へは入れない** —— 本文の原典は取り込み文書（外部データソース由来）であり、Wiki.js 側の sanitize は
管理 UI のトグル 1 つで外れる。ここでの sanitize は**多層防御**である（SC-03 が「HTML 化はサニタイズ方針の決定を伴う」
として Markdown を生で出している論点を、ここでは決めて塞ぐ）。

- **落とすもの**: DOMPurify 既定（`script` / `style` / `iframe` / `object` / `embed` / `form` / イベント属性 /
  `javascript:`）に加え、**メディア（`img` / `picture` / `source` / `video` / `audio`）**と **`target` 属性**。
  - メディアを落とす理由: Wiki.js の資産は SPA オリジンから到達できず（資産のプロキシは無い）、壊れた画像を並べる
    より落とす。**外部 URL の資産をブラウザに取りに行かせない**（08_data-egress-policy の趣旨）ことも兼ねる。
  - `target` を落とす理由: 新規タブを開かない。Wiki.js 本体 UI への外部遷移を画面から無くした決定 1 と同じ向き。
- **書き換えるもの**: Wiki.js の正準パス `/(<locale>/)?doc/<documentId>` を指す `href` は `/wiki?doc=<documentId>` へ。
  ページ間リンクが SPA 内（`?doc=` → by-doc）で解決し、UC-07 基本フロー 1「開く」が本文中からも成り立つ。
  それ以外の `href` は触らない。
- `dompurify` は `@knowledge/frontend` の依存に足す（外部 CDN ではなく npm。`check-static-egress.js` の射程内）。
  **専用インスタンス**（`DOMPurify()`）にフックを足す —— 既定インスタンスに足すと他の呼び出し側へ漏れる。
- `dompurify` は SC-04 の遅延チャンクにだけ載る（[[IADR-0134]] 決定 1。初期チャンクに入れない）。

### 決定 4: 開いているページと検索語は **URL の検索パラメータ**が単一情報源

ルートは計画どおり `/wiki` の 1 本のまま、`page=<slug>` / `doc=<documentId>` / `q=<検索語>` で状態を持つ
（SC-02 の `?q=` / SC-19 の `?tab=` と同じ作法。[[IADR-0124]] 決定 3）。共有・再読込・戻るで同じ画面になる性質を
クライアントストアで二重に持たない。`validateSearch` が外部由来の値を正規化し（文字列でない・空は `undefined`）、
`page` と `doc` が同時に来たら `page` を優先する。**`doc=` は文書別ディープリンク**であり、SC-01 の出典と SC-03 の
「Wiki で閲覧」はここへ送る（SC-03 画面仕様書 §未決事項 4 が解ける）。

### 決定 5: 存在秘匿は中立の文で、**故障は空で隠さない**

一覧・検索の **200 ＋ 空**と本文の **404** は「権限が無い」と「無い」を区別せず中立の文で描く（[[IADR-0009]]）。
**502（Wiki.js 不達）は `Alert`（danger）**で描く —— 「壊れている」は「無い」と別の軸である（[[IADR-0355]] 決定 5）。
401 は `apiClient` の再ログイン導線に委ね、画面は何も足さない。

## 検討した代替案

| 案 | 却下の理由 |
| --- | --- |
| 出典種別を `sourceUri` の接頭辞（`wikiBaseUrl`）で判定し続ける | 決定 1（stg/prod で `WIKI_BASE_URL` を設定しない）により本番で一度も真にならない。統制を外す設定を前提にした判定である |
| 出典種別を by-doc の 200/404 で判定する | 出典 N 件ぶんの往復。本文（HTML）ごと返る面を存在判定に使う |
| 台帳が未取得のとき `wiki` へ倒す | 到達できない導線へ送る。「Wiki かもしれない」を推測しない方針（従前と同じ）を崩す |
| ページツリーを `wikiPath` から階層へ組み立てる | 台帳は平坦（`doc/<id>`）で階層の情報源が無い。捏造になる |
| 本文を SPA 側で Markdown から再レンダリングする | ADR-0073 決定 2 の逐語に反する。Wiki.js の描画（拡張記法・見出し番号等）を再実装することになる |
| Wiki.js の HTML をそのまま `innerHTML` へ入れる | Wiki.js 側の sanitize は管理 UI のトグル 1 つで外れる。原典は外部由来である |
| 画像を残す（`img` を通す） | Wiki.js の資産は SPA オリジンから到達できず、外部 URL の資産をブラウザに取りに行かせる |
| 開いているページをクライアントストアへ持つ | URL と二重になる。共有・再読込・戻るで同じ画面にならない |
| 共通シェルの左レールをページツリーへ置換する（計画 §左ナビ） | シェルは platform の射程で本 issue の宣言領域に無い。画面内の側柱に留め、画面仕様書 §未決事項へ残す |
| 502 を空の一覧・0 件として描く | 「Wiki に何も無い」と読ませ、権限で消えたのか壊れているのかを利用者が区別できない |

## 結果

- **SC-04 が基盤 SPA の中でページツリー・本文・検索を描く。** `target="_blank"` は `sc04-wiki/**` に 0 本
  （受け入れ基準の grep）。画面は `wikiBaseUrl` を読まない（`src/knowledge` のコードから撤去。残る 3 件は
  「廃止した」と説明するコメント行）。**本番（`WIKI_BASE_URL` 未設定）で SC-04 が機能する** ——
  ADR-0073 §残るもの「SC-04 の画面実装が入るまで、本番で Wiki 閲覧ができない」が解けた。
- **SC-01 の 📖 と SC-03 の「Wiki で閲覧」が台帳ベースになり、本番で初めて出る。** どちらも `/wiki?doc=<id>` へ送る。
- `router.test.ts` の SC-04 を `SCREENS_NOT_IN_THE_ROUTE_TABLE` から `PLANNED_ROUTES` へ移した
  （`check-route-manifest.js` 判定 1。17 画面 / 17 行）。
- パンくずは「Wiki」を**親の段**（`/wiki` へのリンク）に置き、葉（題名）は画面が渡す。共通シェルの `breadcrumbTrail()` に
  「葉が無く、末尾の親の段が自ルートを指すなら現在地へ格下げする（リンクにしない）」を足した —— ページを開いていない
  `/wiki` で「いま居る画面へのリンク」が現在地の位置に立たないため（作業仕様書 §実測で見つけて直したもの）。
- **Vitest 24 件**（`WikiBrowsePage` 15 / `sanitizeWikiHtml` 6 / `citations` 3）＋ SC-01 / SC-03 の既存テストを
  台帳ベースへ書き換え（陽性・陰性の対）。**Playwright 3 件**（未認証 → `/login`、セッション付きの
  ツリー → 本文 → 検索、権限外 404 の中立表示 ＋ ツリーの陽性対照。[[IADR-0337]] の作法）。
- `deploy/local/README.md` §Wiki 閲覧の到達 を書き直した —— 「BFF 経由ではない」を消し、**dev では ABAC の統制が
  働かない**と書いた（「dev だから安全」とは書かない。planning#286 裁定の型）。`values.yaml` `frontend.extraEnv` の
  コメントに **stg/prod で `WIKI_BASE_URL` を設定しないことが統制**（機械検査は無く構成の規律）と書いた。
- `WIKI_BASE_URL` の config 項目そのものは残した（ADR-0073 決定 5。dev の管理 UI の到達先を示す手引き）。

### 残るもの

- **共通シェルの左レール置換**（計画 §左ナビ「閲覧時は左レールを Wiki ページツリーへ置換する」）。シェル側の作業として切る。
- **バックリンク欄・ローカルグラフ**は計画が未確定（前提だけが「SPA 側で描く」へ変わった）。
- **Wiki.js の資産（画像）**は本文から落としている。必要になれば資産のプロキシを別 issue で切る。
- **Wiki.js での編集の是非**は未決（ADR-0073 決定 6）。
- 🔴 **local の Wiki.js 直接露出は残る**（決定 5）。そこでは ABAC の統制が働かない。

## 関連

- Supersedes: **なし**。[[IADR-0020]]（前段ゲートウェイ）／[[IADR-0032]]（dev 露出）／[[IADR-0335]]（検索の委譲）／
  [[IADR-0355]]（BFF 中継）は**すべて有効なまま**。[[IADR-0355]] §結果「画面は依然として外部リンク 1 本」だけが
  本 IADR で真でなくなった（同 IADR へ日付つき追記）。
- Issue: #1200（本決定）／#1199（口。[[IADR-0355]]）／#130（従前の画面）／#344（`WIKI_BASE_URL` の edge 整合）
- 作業仕様書: `.ai-context/specs/20260903_issue-1200_sc04-wiki-screen-via-bff.md`
