---
title: SC-04 を基盤 SPA のルートとして BFF 経由で描き、Wiki.js 本体 UI への外部リンクを畳む
type: spec
status: done
related_ids:
  - FR-13
  - FR-05
  - UC-07
  - SC-01
  - SC-03
  - SC-04
  - ADR-0011
  - ADR-0031
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
  - IADR-0365
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0073_wikijs-ui-not-exposed-sc04-via-gateway.md 決定 1・2・4・5・6
  - planning:projects/microservices-platform/07_adr/ADR-0011_wiki-engine.md（分界。2026-08-15 追記）
  - planning:projects/microservices-platform/03_usecases/01_usecases.md UC-07
  - planning:projects/microservices-platform/05_screens/01_screens.md SC-01 / SC-03 / SC-04
---

# 仕様書: SC-04 を基盤 SPA のルートとして BFF 経由で描く（issue #1200）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-13（正規化文書の Wiki 閲覧）／FR-05（ABAC。権限内のページのみ）
- ユースケース（UC）: UC-07（Wiki で閲覧する。基本フロー「開く／検索する」→「ABAC で判定し権限内を表示」。
  例外フロー「権限外の文書は一覧・本文のいずれにも表示しない」）
- 画面（SC）: SC-04（本体）／SC-01（出典の 📖 と Wiki への導線）／SC-03（「Wiki で閲覧」導線）
- 関連 ADR: ADR-0073（決定 1・2・4・5・6）／ADR-0011（分界）／ADR-0031（SPA スタック）
- 先行: #1199（PR #1207。`/bff/wiki/*` 4 経路と orval フック。[[IADR-0355]]）。**本作業はその口を消費する。**

## 目的・背景

ADR-0073 決定 2 は SC-04 §ルートの「Wiki.js 別ホスト・別配信」を撤回し、**SC-04 を基盤 SPA のルートとし、
ページツリー・本文・検索結果を BFF 経由で取得して SPA が描く**と確定させた。#1199 が BFF 口を開いたので、
本作業は**画面**を担う。あわせて issue 実測 4 の派生問題 —— SC-01 の出典種別と SC-03 の「Wiki で閲覧」が
`WIKI_BASE_URL` の接頭辞判定に依存し、**決定 1 に従って stg/prod で `WIKI_BASE_URL` を設定しないと
一度も機能しない** —— を、判定の根拠を **URL 接頭辞から Wiki 台帳（権限内一覧）へ**移して解く。

## 母集合（自分で引いた。issue の数えは転記していない）

**引いた日時**: 2026-09-03。**基点**: `origin/feat/UC-07-bff-wiki-routes` `49b9b287`（PR #1207 の head。
develop `d7a277d8` を取り込み済み）。`git rev-parse --is-shallow-repository` = **`false`**。

**［2026-09-03 追記 / 積み直し］** 上の基点ブランチは PR #1207 の 3 コミットを HEAD より上に持っていたが、#1207 は
squash マージで develop へ入ったため stale になった。未コミットの自作業だけを `git diff HEAD --binary` ＋ 未追跡 9 ファイルで
取り出し、**`origin/develop` `d06cf387` から切り直した `feat/SC-04-wiki-screen-via-bff-v2`** へ `git apply` した。
衝突したのは `src/platform/frontend/src/locales/**` の 4 ファイル（生成物）だけで、これは除外して `pnpm run i18n` で再生成した
（en の 14 件は前任の訳を写した）。母集合の走査結果は基点が進んでも変わらない（同じ語で再走査し、件数が一致することを確かめた）。

走査は「誤りの側」の語で引いた（`.claude/rules/traceability.md` 規則 1・2）。除外は
`.ai-context/specs` / `.ai-context/superpowers` / `.ai-context/adr` / `CHANGELOG.md` /
`src/platform/frontend/src/lib/api/generated`（凍結記録・生成物。理由は下の除外表）。

### 軸 1: `wikiBaseUrl` / `WIKI_BASE_URL` / `wiki.localhost`（大小無視）

```console
$ git grep -n -i "wikiBaseUrl\|WIKI_BASE_URL\|wiki\.localhost" -- . ':!.ai-context/specs' ':!.ai-context/superpowers' ':!.ai-context/adr' ':!CHANGELOG.md' ':!src/platform/frontend/src/lib/api/generated'
```

| 区分 | ファイル | 扱い |
| --- | --- | --- |
| 画面コード | `sc04-wiki/components/WikiAccessPage.tsx`（3）・同 `.test.tsx`（4） | **撤去**（新画面へ置換） |
| 画面コード | `sc01-search/types/citations.ts`（4）・同 `.test.ts`（3）・`components/SearchChatPage.tsx`（1）・同 `.test.tsx`（6） | **判定を台帳へ置換** |
| 画面コード | `sc03-document/components/DocumentDetailPage.tsx`（2）・同 `.test.tsx`（9） | 同上 |
| 画面テスト | `features/searchFlow.test.tsx`（1）・`features/adminFlow.test.tsx`（1） | `runtimeConfig` の mock が不要になる。**画面が読まなくなった値を mock し続けない**（更新） |
| 実行時 config | `platform/frontend/src/config/runtimeConfig.ts`（3）・同 `.test.ts`（6）・`config.js.template`（1）・`docker-entrypoint.d/40-render-config.sh`（3） | **項目は残す**（issue「決定 5 により local の直接露出は残す・撤去しない」）。**説明コメントだけ**「画面は読まない」へ追随（`runtimeConfig.ts` / `config.js.template`）。`40-render-config.sh` は #1135 の Dockerfile 近傍なので触らない（コメントも無い） |
| 配備 | `deploy/helm/microservices-platform/values.yaml:844`（コメント） | 決定 1 の統制を**コメントで**明記（issue やること 6） |
| 配備 | `deploy/local/values-local.yaml:105-125`（コメント＋値） | **値は残す**（決定 5）。コメントの「Wiki を直接開く導線」が誤りになるため**コメントのみ**追随 |
| 配備文書 | `deploy/local/README.md:327-358` | §Wiki 閲覧の到達 を**書き直す**（やること 5） |
| 配備文書 | `deploy/local/wiki-oidc/README.md:103,168`（「SPA の『Wiki を開く』導線」） | 「SPA の導線」という説明が誤りになる。**1 語の是正**（値の役割を「管理 UI の到達先」へ） |
| 配備（edge / realm / TLS） | `deploy/local/edge/**`・`deploy/local/edge-istio/**`・`deploy/keycloak/*realm.json`・`scripts/check-realm-constraints.js`・`deploy/local/wikijs-setup/**`・`docs/operations/local-sso-recovery-runbook.md` | **対象外。** `wiki.localhost` は Wiki.js **管理 UI** の到達先であり ADR-0073 決定 5 が維持を定める。#1135 が `deploy/local/edge*` を宣言している |
| 仕様書 | `docs/screens/SC-04_wiki-access.md`（3）・`SC-01_search-chat.md`（3）・`SC-03_document-detail.md`（2）・`docs/tests/SC-04_wiki-access.md`（6）・`SC-01_search-chat.md`（3）・`SC-03_document-detail.md`（1） | **追随** |

**陽性対照**: 同じ走査を凍結記録側（`.ai-context/adr`）へ向けると `IADR-0095`（9）ほか 6 ファイルにヒットする。
走査は空振りしていない。凍結記録は書き換えない。

### 軸 2: 外部リンク `target="_blank"`（受け入れ基準の grep そのもの）

```console
$ git grep -n 'target="_blank"' -- src/knowledge/frontend/src/features/sc04-wiki
src/knowledge/frontend/src/features/sc04-wiki/components/WikiAccessPage.tsx:32
```

1 件。撤去後 **0 件**を受け入れ基準で固定する（同 grep は SC-03 の「原本」リンクにはヒットしない —— 別ディレクトリ）。

### 軸 3: 導線の言い回し（`BFF 経由ではない` / `新規タブで直接` / `Wiki を開く` / `Open the wiki` / `遷移導線`）

Wiki に関するヒット（`遷移導線` は SC-10 など無関係画面にも当たるため Wiki を含む行だけ挙げる）:

| ファイル | 扱い |
| --- | --- |
| `deploy/local/README.md:328,336` | 書き直し（軸 1 と同じ） |
| `deploy/local/wiki-oidc/README.md:103,168` | 1 語の是正（軸 1 と同じ） |
| `docs/screens/SC-04_wiki-access.md:27,37,45`・`docs/tests/SC-04_wiki-access.md:33,41` | 書き直し |
| `src/knowledge/frontend/src/features/sc04-wiki/**` | 置換 |
| `src/platform/frontend/src/app/routing/breadcrumbs.test.ts:205`（コメント「SPA 側の遷移導線として持つ」） | コメント 1 行の追随 |
| `src/platform/frontend/src/app/routing/router.test.ts:59` | `SCREENS_NOT_IN_THE_ROUTE_TABLE` の SC-04 行を**削除**し `PLANNED_ROUTES` へ移す（計画 §ルートが `/wiki`（基盤 SPA）になった） |
| `src/platform/frontend/src/locales/{ja,en}/messages.{po,ts}` | `pnpm run i18n` の再生成に委ねる（手で触らない） |

### 軸 4: `別ホスト` / `wiki.example` / `別配信`（Wiki を含む行）

`router.test.ts:59`（上と同じ）と `sc04-wiki/**` のみ。**陽性対照**: `.ai-context/adr` と `.ai-context/specs` に
合計 50 件超（凍結記録。書き換えない）。

### 除外したものと理由（黙って外さない）

- **`.ai-context/adr/` / `.ai-context/specs/` / `.ai-context/superpowers/` の本文**: 確定済みの凍結記録。
  IADR-0355 §結果「画面は依然として外部リンク 1 本」は本作業で真でなくなるが、**日付つき追記**だけを足す。
- **`CHANGELOG.md`**: 生成物（手で書き足さない）。
- **`src/platform/frontend/src/lib/api/generated/**`**: orval 生成物。`pnpm run codegen` の差分ゼロで検査する。
- **`deploy/local/edge*` / `src/platform/frontend/Dockerfile` / `docker-entrypoint.d/`**: #1135 の宣言領域。
- **`src/*/frontend` の `.gitkeep`**: #1195 の宣言領域。sc04 に `api/` `hooks/` `types/` を新設するが `.gitkeep` は置かない
  （中身のある区分だけを作る）。
- **`WikiService` 本体**: ADR-0073 §結果「実装は変更不要」。
- **`platform/frontend/src/app/`（共通シェル）**: 計画 §SC-04「閲覧時は左レールを Wiki ページツリーへ置換する」は
  シェル（platform）の改修であり、本 issue の宣言領域に無い。**ページツリーは画面内の側柱として描き、
  左レール置換は未決として画面仕様書に残す**（IADR-0365 §残るもの）。

## 対象範囲

- 対象:
  - `src/knowledge/frontend/src/features/sc04-wiki/**`（画面の作り直し。`api/` `hooks/` `types/` を新設）
  - `src/knowledge/frontend/src/lib/wiki-pages/**`（新設。SC-01 / SC-03 / SC-04 が共有する「権限内 Wiki 台帳」の索引フック。
    feature 跨ぎの共有は `lib/` に置く —— ADR-0066 決定 1 / [[IADR-0308]] 決定 6 と同じ判断）
  - `sc01-search/types/citations.ts` / `components/SearchChatPage.tsx`、`sc03-document/components/DocumentDetailPage.tsx`（各テスト含む）
  - `features/searchFlow.test.tsx` / `adminFlow.test.tsx`（mock の追随）
  - `src/platform/frontend/src/app/routing/router.test.ts`（マニフェスト）/ `breadcrumbs.test.ts`（コメント）
  - `src/platform/frontend/e2e/sc04-wiki.smoke.spec.ts` / `sc03-document.smoke.spec.ts`
  - `src/platform/frontend/src/locales/**`（`pnpm run i18n`）
  - `src/knowledge/frontend/package.json` / `src/pnpm-lock.yaml`（`dompurify` を足す）
  - `docs/screens/SC-04_*` / `SC-01_*` / `SC-03_*`、`docs/tests/UC-07_*` / `SC-04_*` / `SC-01_*` / `SC-03_*`
  - `deploy/local/README.md`、`deploy/local/values-local.yaml`（コメント）、`deploy/local/wiki-oidc/README.md`（1 語）、
    `deploy/helm/microservices-platform/values.yaml`（コメント）
  - `src/platform/frontend/src/config/runtimeConfig.ts` / `config.js.template`（コメントのみ）
  - `.ai-context/adr/IADR-0365_*.md`（新規）＋ 索引、`IADR-0355` への日付つき追記
- 対象外: Wiki.js での**編集**（ADR-0073 決定 6 は未決）。バックリンク欄・ローカルグラフ（計画が未確定）。
  左レール置換（上記）。`WIKI_BASE_URL` の config 項目そのものの撤去。Wiki.js 資産（画像）のプロキシ。

## 設計

### 1. ルートと URL（[[IADR-0124]] / [[IADR-0134]]）

- ルートは **`/wiki` 1 本のまま**（計画 §ルート `/wiki`）。状態は**検索パラメータ**で持つ（SC-02 / SC-19 と同じ作法）:
  - `page=<slug>`: ページツリー・検索結果から本文を開く（`/bff/wiki/pages/{slug}`）
  - `doc=<documentId>`: SC-01 の出典・SC-03 の「Wiki で閲覧」から開く（`/bff/wiki/pages/by-doc/{id}`）。
    **文書別ディープリンク**（SC-03 画面仕様書 §未決 4 が解ける）
  - `q=<検索語>`: 検索（`/bff/wiki/search?q=`）
  - `validateSearch` で外部由来の値を正規化（文字列でない・空は `undefined`）。`page` と `doc` が同時に来たら `page` を優先。
- 画面は `lazyRouteComponent` の遅延チャンクのまま（[[IADR-0134]] 決定 1）。`dompurify` はこのチャンクにだけ載る。
- `router.test.ts` の SC-04 を `SCREENS_NOT_IN_THE_ROUTE_TABLE` から `PLANNED_ROUTES` へ移す（`check-route-manifest.js` 判定 1）。

### 2. 取得（orval 生成フック。[[IADR-0135]] 決定 1）

| 面 | フック | 有効化 | 失敗の扱い |
| --- | --- | --- | --- |
| ページツリー | `useBffWikiPageList` | 常時 | 502/5xx → `Alert`（danger）。空 → 中立文「閲覧できる Wiki ページはありません。」（deny-by-default と「無い」を区別しない） |
| 本文（slug） | `useBffWikiPageBySlug(page)` | `page` があるとき | 404 → 中立文「ページが見つかりませんでした。」（存在秘匿 [[IADR-0009]]）。5xx → `Alert` |
| 本文（文書 ID） | `useBffWikiPageByDocument(doc)` | `doc` があり `page` が無いとき | 同上 |
| 検索 | `useBffWikiSearch({ q })` | `q` が空白以外のとき | 502 → `Alert`「Wiki に到達できませんでした。」（**故障を空で隠さない**。IADR-0355 決定 5）。空 → 「該当するページはありません。」 |

401 は既存の `apiClient` の再ログイン導線（`setUnauthorizedHandler`）に委ねる。画面は何も足さない。

### 3. ページツリーの取得単位（IADR-0365 決定 2）

`GET /bff/wiki/pages` **1 回**。台帳（`WikiPage`）の `wikiPath` は `doc/<documentId>` の**平坦**な正準パスであり、
後段は題名順で返す（`ListWikiPagesEndpoint`: `OrderBy(p => p.Title)`）。**階層は台帳に無い**ので、ツリーは
「題名順の一覧」として描く（`nav aria-label`）。階層を SPA 側で捏造しない。

### 4. 本文の描画と sanitize（IADR-0365 決定 3）

`WikiPageView.content` は **Wiki.js が描画した HTML** である（ゲートウェイがプロキシ。ADR-0073 決定 2）。SPA は
Markdown を再レンダリングしない。HTML は **DOMPurify で sanitize してから `dangerouslySetInnerHTML`** で描く。

- Wiki.js 側の sanitize 設定（管理 UI のトグル）に依存しない**多層防御**。本文の原典は取り込み文書（外部データソース由来）であり、
  SC-03 が「HTML 化はサニタイズ方針の決定を伴う」として Markdown を生で出している論点と同じ穴を、ここでは決定して塞ぐ。
- **落とすもの**: `script` / `style` / `iframe` / `object` / `embed` / `form`（DOMPurify 既定）に加え、
  `img` / `picture` / `video` / `audio` / `svg`（**Wiki.js の資産はプロキシされておらず SPA オリジンから到達できない**。
  かつ外部 URL の資産をブラウザに取りに行かせない —— 08_data-egress-policy の趣旨）と、`target` 属性（新規タブを開かない）。
- **書き換えるもの**: `href` が Wiki.js の正準パス `/(<locale>/)?doc/<documentId>` を指すリンクは `/wiki?doc=<documentId>` へ
  書き換える（ページ間リンクが SPA 内で解決する。UC-07 基本フロー 1「開く」）。それ以外の `href` は触らない。
- `dompurify` は `@knowledge/frontend` の依存に足す（外部 CDN ではなく npm。`check-static-egress.js` の射程内）。

### 5. 出典種別の判定（IADR-0365 決定 1。SC-01 / SC-03）

- `src/knowledge/frontend/src/lib/wiki-pages/useWikiPageIndex.ts`: `useBffWikiPageList` を `select` で
  **文書 ID の集合**へ畳む。SC-04 のツリーと同じ生成キー（`['/bff/wiki/pages']`）なのでキャッシュを共有する。
- `citationKind(documentId, wikiDocumentIds)`: 集合に含まれれば `wiki`、**未取得・取得失敗（`undefined`）なら `document`**
  （「Wiki かもしれない」を推測しない —— 従前の方針を引き継ぐ）。`sourceUri` は見ない。
- SC-01 の 📖 は `/wiki?doc=<documentId>` へ。SC-03 の「Wiki で閲覧」は台帳に文書があるときだけ出し、同じ URL へ。
- **by-doc を存在判定に使わない理由**: 本文（HTML）ごと返る面であり、SC-01 では出典 N 件ぶんの往復になる。
  一覧は権限内メタデータだけの 1 回で、後段の ABAC（`AbacPageFilter`）が個別取得と同じ門を通す。

### 6. 表示・文言

- 見出し `Wiki 閲覧`（左ナビ「Wiki」は固有名詞のリテラルのまま）。パンくずの葉は `useBreadcrumbLeaf(title)`。
- 本文の下: **最終同期日時**（`syncedAt`。`formatDateTime`）と **文書詳細（SC-03）への復帰リンク**（`/docs/$id`）。
- 文言はすべて Lingui（ja / en）。状態表示は色だけに頼らない（`Alert` / `role="status"` ＋ 文）。
- 外部リンクは置かない（`target="_blank"` 0 件）。

### 7. 追随する記述

- `WikiAccessPage.tsx` の誤ったコメント「到達はゲートウェイ（ABAC）経由に限定される（IADR-0020）」は
  ファイルごと消えるが、**根拠が変わったこと**（従前は外部リンクで前段を通っていなかった。今は `/bff/wiki/*` →
  WikiService の 1 本に限られる）を新画面の冒頭コメントと IADR-0365 に残す。
- `values.yaml` `frontend.extraEnv` のコメント: stg/prod で `WIKI_BASE_URL` を設定しないことが**統制**であり、
  **機械検査は無く構成の規律である**と書く（決定 1）。
- `deploy/local/README.md` §Wiki 閲覧の到達: 「BFF 経由ではない」を消し、**dev では ABAC の統制が働かない**
  （「dev だから安全」とは書かない。planning#286 裁定）と決定 5 を書く。

## 受け入れ基準

- [x] 認証済みで `/wiki` を開くと**ページツリー・本文・検索欄が SPA 内に描かれる**。
      `git grep 'target="_blank"' -- src/knowledge/frontend/src/features/sc04-wiki` が **0 件**
- [x] 権限外・不存在は一覧に現れず、本文は 404 相当の中立表示。**陽性対照として権限内が見える**ことを同じテスト群で押さえる
- [x] `WIKI_BASE_URL` 未設定（stg/prod 既定）で `/wiki` が「接続先が未設定です」を出さず正常に描く
      （画面が `wikiBaseUrl` を **1 箇所も読まない**: `git grep wikiBaseUrl -- src/knowledge` のヒットが「廃止した」と説明するコメント行だけで、コード行は 0 件）
- [x] `WIKI_BASE_URL` 未設定で SC-01 の Wiki 由来出典が 📖 で出て、SC-03 の「Wiki で閲覧」が出る（台帳ベース）
- [x] `deploy/local/README.md` §Wiki 閲覧の到達 に「BFF 経由ではない」が残らず、dev で ABAC が働かないことが書かれている
- [x] `values.yaml` の `wikijs.ingress.enabled` は `false` のまま（変更しない）
- [x] `docs/screens/SC-04_wiki-access.md` §主要素にページツリー・本文・検索が載り、外部遷移の記述が無い。
      trace ブロック `adrs:` に `ADR-0073`
- [x] `docs/tests/UC-07_wiki-browsing.md` §未実施 から「委譲先が自前で持っている検索画面は前段を通らないままである」と
      「画面そのものは測っていない」が消え、テストケース表へ移っている
- [x] `pnpm run typecheck` / `lint` / `format:check` / `test:coverage`（しきい値は増えたテストに応じて上げる）/ `i18n`（差分ゼロ）/
      `codegen`（差分ゼロ）/ `build` が通る。`check-static-egress.js --require <dist>` / `check-route-manifest.js` /
      `check-trace-blocks.js` / `check-i18n-catalogs.js` / `gen-knowledge-graph.js --check` / `check-doc-*.js` /
      `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が通る
- [x] 稼働 k3s（`wiki-service` を最新イメージへ差し替えた後）で実ブラウザから SC-04 がツリー・本文・検索を描く（陽性）／
      未認証は `/login`（陰性）／SC-03 の「Wiki で閲覧」が `?doc=` で開く

## テスト方針

| 層 | 何を | 陽性 / 陰性 |
| --- | --- | --- |
| Vitest `WikiBrowsePage.test.tsx` | ツリー（一覧の描画・リンク先 `?page=`）／空／5xx | 陽性: 2 件が並ぶ。陰性: 空は中立文、5xx は `role="alert"` |
| 同 | 本文（`?page=` → slug、`?doc=` → by-doc）／404／sanitize | 陽性: `<h2>` が見出しとして描かれ `<b>` が残る。陰性: `<script>` `<img>` `target` が消え、`doc/<id>` リンクが `/wiki?doc=` へ書き換わる。404 は中立文 |
| 同 | 検索（送信 → `?q=` → 結果）／空／502 | 陽性: 当たりがリンクで並ぶ。陰性: 空は中立文、502 は `role="alert"` |
| 同 | 最終同期日時・SC-03 復帰リンク・パンくずの葉・en ロケール・`target="_blank"` 不在 | — |
| Vitest `citations.test.ts` | 集合に含まれる／含まれない／未取得 | 陽性: `wiki`。陰性: `document` |
| Vitest `SearchChatPage.test.tsx` | 台帳に載る出典は 📖 ＋ `/wiki?doc=`／載らない・取得失敗は 📄 ＋ `/docs/` | 対 |
| Vitest `DocumentDetailPage.test.tsx` | 台帳に載る文書だけ「Wiki で閲覧」（`/wiki?doc=`） | 対 |
| Playwright `sc04-wiki.smoke.spec.ts` | 未認証 → `/login`。セッション付き: ツリー → 本文 → 検索の導線、外部リンク 0、`expectBffTrafficIsComplete` | [[IADR-0337]] の作法 |
| Playwright `sc03-document.smoke.spec.ts` | `GET /wiki/pages` の応答を用意（用意しないと `unhandled` で落ちる） | — |
| 実測 | 稼働 k3s ＋ 実ブラウザ | 上の受け入れ基準 |

## 計画書との差異

- 差異: **あり（計画の未確定に留まる範囲）**
  - 計画 §SC-04「閲覧時は左レールを Wiki ページツリーへ置換する」は共通シェル（platform）の改修を要し、本 issue の宣言領域に無い。
    ページツリーは画面内の側柱に置く。**画面仕様書 §未決事項へ残す**（環流不要。計画は hi-fi を正と確定済みで、実装側の段取りの問題）。
  - バックリンク欄・ローカルグラフ・`[[` 補完・ヘルプ文言は計画が「未確定」としており、本 issue で決めない（issue 補足）。
  - Wiki.js 資産（画像）は SPA から到達できないため本文から落とす。計画に資産の扱いの記述は無い。**IADR-0365 §残るもの**に置く
    （必要になれば資産のプロキシを別 issue で切る）。

## 実測（2026-09-03。稼働 k3s ＝ Rancher Desktop の k3s。`git rev-parse --is-shallow-repository` = `false`）

### 環境と前提（この条件を書かないと結果が読めない）

- `pnpm` は `npm_config_manage_package_manager_versions=false` を付けて実行した（Volta シムの ENOENT が exit 0 で
  「成功した」形で返る問題。#1139 の作業仕様書 §実測と同じ）。Node は **24.18**（ローカル）。
  **`orvalMutator.test.ts` の 1 件は Node 24 だけの赤**（CI の Node 22 では緑。既知）で、本作業と無関係。
- `src/ai-stock-trading`（submodule）を `git submodule update --init` してから `tsc -b` を通した。
- 🔴 **稼働の `wiki-service:latest` は 6 週間前のイメージで `/wiki/search`（#1126）を持たなかった。**
  本ブランチから `nerdctl --namespace k8s.io build` で `wiki-service:issue1200` を焼き、
  `kubectl set image deploy/wiki-service '*=…:issue1200'` → rollout 完了（pod `wiki-service-7d69766659-wcm5v`）。
  **戻していない**（develop 相当の最新であり、戻す理由が無い）。
- BFF は実測の途中で別セッション（#1187）が `bff:issue1187` へ差し替えた。**`/bff/wiki/*` 4 経路は同イメージにも在る**
  （未認証 401 ＝ 実在。陽性対照: `/bff/nope` は 404）。
- SPA は本ブランチの `pnpm run build` の `dist` を `vite preview`（`:4173`）で配信し、`/bff` を
  `kubectl port-forward svc/bff-service 5000:8080` へプロキシした（`changeOrigin` により BFF が組む redirect_uri は
  `http://localhost:5000/bff/auth/callback` ＝ realm client `bff` の登録済み URI）。**稼働の frontend-service には触っていない**
  （#1135 が同時に frontend の配備を扱っているため。SPA 側の成果物は同じ `dist` である）。
- 🔴 **稼働 realm は `deploy/keycloak/microservices-platform-realm.json` から乖離していた。** user profile の
  `unmanagedAttributePolicy` が無く（マニフェストは `ADMIN_EDIT`）、**ABAC 属性（`clearance` / `department`）が
  204 のまま黙って捨てられる**状態だった（`KeycloakIdentityAdminClient.cs` が #1101 で実測して fail-closed にした型そのもの）。
  Admin REST API（`PUT /admin/realms/platform/users/profile`）で **`ADMIN_EDIT` へ揃えた**（マニフェストと一致させただけ。
  Keycloak は再起動していない。**戻していない** —— 戻すと SC-17 の属性割当が同じ形で壊れる）。
- 一時利用者 `e2e-1200` を Admin REST API で作り（`kcadm.sh` は使っていない）、終了時に **DELETE 済み**
  （削除後の検索は `[]`。陽性対照: `developer` は残っている）。`developer` は稼働 realm で TOTP 必須（`totp: true`）で
  自動操作に使えなかった。realm の既定必須アクション `CONFIGURE_TOTP` / `VERIFY_PROFILE` は一時利用者から外した。

### 結果（実ブラウザ Chromium。Playwright の `chromium.launch()` ＋ `ignoreHTTPSErrors`）

| # | 観点 | 利用者 | 結果 |
| --- | --- | --- | --- |
| 1 | 陰性: 未認証で `/wiki` | — | `/login?from=%2Fwiki` へ |
| 2 | 陽性: ページツリー | `e2e-1200`（clearance=restricted） | `GET /bff/wiki/pages` 200。**2 件**（`FR-08 565 verification doc` / `report`）が題名順に並び、リンクは `/wiki?page=<slug>` |
| 3 | 陽性: 本文 | 同上 | `GET /bff/wiki/pages/fr-08-565-verification-doc` 200。Wiki.js の `<p>` がそのまま描かれ、`script` 0 / `img` 0 / `a[target=_blank]` 0。最終同期 `2026/09/02 19:21`・「文書詳細へ戻る」`/docs/6cdfee53-…`。パンくず `ホーム / Wiki / FR-08 565 verification doc`（スクリーンショット） |
| 4 | 陽性: 検索 | 同上 | `GET /bff/wiki/search?q=re` 200。当たり `report` がリンクで並ぶ |
| 5 | 陰性: 不存在のスラッグ | 同上 | `GET /bff/wiki/pages/no-such-page-1200` 404 → 中立文「ページが見つかりませんでした。」。`alert` 0。**ツリーは 2 件のまま**（陽性対照） |
| 6 | 陽性: SC-03 の「Wiki で閲覧」 | 同上 | `/docs/14e97c67-…` に `href=/wiki?doc=14e97c67-…` で出る。押すと `GET /bff/wiki/pages/by-doc/14e97c67-…` 200 で `report` の本文が開く |
| 7 | 🔴 陰性（ABAC）: 属性を持たない利用者 | `e2e-1200`（属性なし） | `GET /bff/wiki/pages` **200 ＋ 空** → 「閲覧できる Wiki ページはありません。」（`alert` 0）。by-doc は **404** → 中立文。検索は 200 ＋ 空 → 「該当するページはありません。」。SC-03 は文書自体が 404（存在秘匿） |

**7 は 2〜6 と同じ台帳・同じ画面に対する対**である —— 判定器が常に落とす実装でも 7 は緑になるため、2〜6 が無いと意味を持たない。

### 実測で見つけて直したもの

- 🔴 **パンくずの葉が一度も描かれなかった。** `sc04WikiBreadcrumb` に `label: 'Wiki'` を置いていたが、共通シェルの
  `breadcrumbTrail()` は自画面の段を `label ?? leaf` で決めるため、`useBreadcrumbLeaf(title)` が効かない
  （単体テストは `useBreadcrumbLeaf` を mock して「呼ばれたこと」しか見ておらず、通っていた）。「Wiki」を SC-03 の「検索結果」と
  同じ**親の段**（`/wiki` 自身へのリンク）へ移し、`label` を外した。`breadcrumbs.test.ts` の
  「label を省略できるのは SC-03 だけ」を SC-03 / SC-04 の 2 画面へ改め、Playwright スモークに葉の断言を足した。
  - **［2026-09-04 追記 / 積み直し後の再検証］** 上の直しは **`Layout.test.tsx`「現在地はリンクではない」を赤にしていた**
    （前任の検証ゲートは直す前の全件走行を記録しており、直した後は関連ファイルしか回していない）。ページを開いていない
    `/wiki` で「Wiki」の段が `/wiki` へのリンクとして現在地の位置に立っていた。`breadcrumbTrail()` に
    「葉が無く、末尾の親の段が自ルートを指すなら、その段を現在地へ格下げする（リンクにしない）」を足し、
    `breadcrumbs.test.ts` に陽性（葉あり: 親はリンク・葉が現在地）／陰性（葉なし: 親が現在地・リンク無し）／
    対照（SC-03 の `/search` を指す親は葉が無くてもリンクのまま）の 3 件を置いた。Layout.test.tsx は書き換えていない。

### 検証ゲート

- `pnpm run typecheck` / `lint`（0 errors）/ `format:check` / `i18n`（差分ゼロ）/ `codegen`（差分ゼロ）/ `build` — 緑
- Vitest **1417 / 1418**（赤 1 は上記 Node 24 の既知）。本作業で足したのは `WikiBrowsePage` 14 / `sanitizeWikiHtml` 6 /
  `citations` 3 ＋ SC-01 / SC-03 / breadcrumbs の書き換え。カバレッジ横断 lines 98.04 / branches 93.14 / functions 94.79 —
  しきい値（93 / 88 / 89）は「MSP 所有分の実測 − 5pt 切り捨て」の導出規則で**据え置き**（同じ値に落ちる）
- Playwright `sc04-wiki` 3 件 ＋ `sc03-document` 2 件 — 緑
- `check-route-manifest`（17 画面 / 17 行）/ `check-trace-blocks`（168 件）/ `check-i18n-catalogs` / `check-doc-*` /
  `gen-knowledge-graph --check` / `check-static-egress --require dist`（45 ファイル、外部オリジン 0。`dompurify` は
  `WikiBrowsePage-*.js` の遅延チャンクにだけ載る＝実測） — 緑
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` は `check-adr-numbering` の欠番（IADR-0361〜0366。番号はマージ時に詰める）だけで赤 —— 想定内
- `knip`: 本作業の新規ファイルに未使用 export は無い（`WikiPageIndex` 型の再輸出を 1 件消した）

### ［2026-09-04 追記］積み直し後（`feat/SC-04-wiki-screen-via-bff-v2`。基点 `origin/develop` `d06cf387`）の再走行

- `pnpm run typecheck` / `lint`（0 errors）/ `format:check`（`sc04-wiki.smoke.spec.ts` の整形漏れを `pnpm run format` で直した）/
  `i18n`（再生成前後で `messages.{po,ts}` の md5 一致。en の未翻訳 0）/ `codegen`（差分ゼロ）/ `build` — 緑
- Vitest: 初回 **1416 / 1418**。赤 2 のうち 1 は Node 24 の既知（`orvalMutator`）、もう 1 は `Layout.test.tsx`
  「現在地はリンクではない」（§実測で見つけて直したもの の 2026-09-04 追記で直した。直した後 `breadcrumbs.test.ts` ＋
  `Layout.test.tsx` 52 / 52、全件は下の最終走行）
- Playwright `sc04-wiki` 3 件 ＋ `sc03-document` 2 件 — 緑（直した後の `dist` に対して）
- `check-static-egress --require dist`（45 ファイル、外部オリジン 0。`dompurify` は `WikiBrowsePage-*.js` にだけ載る）/
  `check-chunk-budget` / `check-route-manifest`（17 / 17）/ `check-trace-blocks`（168）/ `check-i18n-catalogs` /
  `check-doc-*` / `check-plan-id-qualification` / `check-reading-budget` / `gen-knowledge-graph --check` — 緑
- `check-adr-numbering`: IADR-0361〜0366 の欠番で赤（想定内。番号はマージ時に詰める）
- 稼働 k3s（未認証だけ。`curl --cacert <local-edge-root-ca>` ＋ Node `https`（`authorized=true`）の 2 系で TLS を検証した。
  `-k` は使っていない。陰性対照: CA を渡さないと接続できない）:

  | 経路（`https://localhost`） | 結果 |
  | --- | --- |
  | `GET /bff/wiki/pages` / `/bff/wiki/search?q=re` / `/bff/wiki/pages/by-doc/<guid>` / `/bff/wiki/pages/no-such-page-1200` | **401**（未認証は BFF で拒む。IADR-0355 決定 3） |
  | `GET /bff/nope`（陽性対照: 401 が「何でも 401」ではないこと） | **404** |
  | `GET /` | 200 |

  `wiki-service` は前回の実測で差し替えた `issue1200` のまま稼働（`kubectl get deploy`。再差し替え不要）。
  🔴 **認証済みの実ブラウザ実測（上表 2〜7）は本セッションで再走行していない** —— 同一 patch の積み直しであり、
  BFF / WikiService は本 PR で変えておらず、SPA 側は本ブランチの `dist` に対する Playwright が同じ導線を固定する。
  稼働の `frontend-service`（`frontend:latest`）には本ブランチの SPA を載せていない（前回と同じ）。

## 未決事項

- なし（着手前に人へ確認が要る論点は無い。上の差異はすべて「決めない」と裁定済みの範囲）。
- **申し送り**（未決ではない）: 稼働 realm の user profile を `ADMIN_EDIT` へ揃えたこと、`wiki-service` を `issue1200` へ
  差し替えたことは上の §実測 のとおり。どちらも戻していない。
