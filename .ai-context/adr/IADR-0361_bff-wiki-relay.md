---
title: IADR-0361 Wiki の BFF 中継は knowledge 側の透過中継とし、ABAC・存在秘匿・クランプを後段の 1 箇所に残す
type: impl-adr
status: Accepted
related_ids:
  - FR-13
  - UC-07
  - SC-04
  - NFR-09
  - ADR-0004
  - ADR-0011
  - ADR-0032
  - ADR-0073
  - IADR-0009
  - IADR-0020
  - IADR-0044
  - IADR-0089
  - IADR-0251
  - IADR-0273
  - IADR-0285
  - IADR-0300
  - IADR-0335
  - IADR-0346
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0073_wikijs-ui-not-exposed-sc04-via-gateway.md 決定 1・2・4
  - planning:projects/microservices-platform/07_adr/ADR-0011_wiki-engine.md（分界。2026-08-15 追記）
  - planning:projects/microservices-platform/03_usecases/01_usecases.md UC-07
  - planning:projects/microservices-platform/05_screens/01_screens.md SC-04
---

# IADR-0361: Wiki の BFF 中継（`/bff/wiki/*`）の形（#1199）

> 🔴 **番号は暫定である。** 起草時点の `develop`（`45853885`）の最大は `IADR-0353` だが、
> **0354〜0360 は進行中の並行 PR へ割当済み**であるため 0361 を仮置きした。**マージ直前に実際の
> 空き番号へ付け直し**、ファイル名・本文の自称番号・索引（`.ai-context/adr/README.md`）・作業仕様書・
> コード内コメント（`WikiBffEndpoints.cs` / `BffEndpointComposition.cs` / `Program.cs` /
> `BffEndpointCompositionTests.cs` / `BffWikiEndpointTests.cs`）・`docs/` の trace ブロック・
> PR タイトルを追随させること（`scripts/check-adr-numbering.js` は昇順・欠番なしを fail で見る）。

- 状態: Accepted
- 日付: 2026-09-03
- 決定者: claude（実装）

## コンテキストと課題

**Wiki 前段（`WikiService`）の 4 経路はすべて実装済みだが、画面用の口が 1 本も無かった。**

[[IADR-0335]] は **「`/bff/wiki/*` は作らない」**と明記していた —— 当時 `SC-04` は
「Wiki.js 別ホスト・基盤 SPA とは別配信」であり SPA は導線しか持たず、既存 3 経路にも BFF 口が
無かったため、**検索だけ露出面が違う状態を作らない**という判断だった。そのうえで
「**露出は SC-04 の実現方式が決まってから 1 回で行う**」とフォローアップに置いていた。

**計画 `ADR-0073`（Accepted / 2026-09-03）がその実現方式を確定させた。**

- 決定 1: 利用者が Wiki の内容へ到達する経路は **`WikiService`（`/wiki/*`）の 1 本**に限る。
- 決定 2: **`SC-04` §ルート の「Wiki.js 別ホスト・別配信」を撤回**し、基盤 SPA のルートとする。
  ページツリー・本文・検索結果を **BFF 経由で取得して SPA が描く**。
- 決定 4: **`/bff/wiki/*` を 4 経路まとめて開く。**
  **「`IADR-0335` が BFF 口を作らなかった判断は正しかった —— 本決定がその『1 回でまとめて行う』
  時点である」**と明記している。

すなわち **`IADR-0335` の判断は覆されたのではなく、前提が満たされて解けた**。
中継そのものは既存の `GraphBffEndpoints` / `PrivateNoteBffEndpoints` / `NotificationBffEndpoints` と
同型で足りる。にもかかわらず記録が要るのは、**「同型で足りる」と判断した根拠のうち 5 つが、
書かないと次の担当者が逆へ倒しうるもの**だからである。

1. **置き場所**（`Knowledge.Bff.Endpoints` か platform 同居か）。
2. **資格情報の伝播方式**（JWT 伝播か、解決済み scope を本文で渡すか）。
3. **BFF 側に ABAC の前段を置くか。**
4. **未認証をどう返すか** —— 🔴 [[IADR-0335]] 決定 4 が「**401 にはしない**」と書いているのに、
   ここでは 401 にする。
5. **検索の既定・上限をどちらが持つか。**

## 決定

### 決定 1: `Knowledge.Bff.Endpoints` に置く（platform 同居にしない）

後段 `WikiService` は **knowledge ユニット**のサービスである
（`src/knowledge/backend/Services/WikiService`）。`TagDictionaryBffEndpoints` / `PrivateNoteBffEndpoints`
（後段 DocumentService）・`GraphBffEndpoints`（後段 GraphService）と同じ切り分けであり、
platform 同居は**後段が platform ユニットのとき**に限る（`McpClient` / `UserAdmin` / `Notification`。
[[IADR-0346]] 決定 1）。**判定軸は「後段のユニット」であって「機能の見た目」ではない** ——
軸を変えると、次に迷う人が別の答えを出す。

### 決定 2: 資格情報は `Authorization` ヘッダの伝播（方式 A）

本リポジトリの BFF には権限伝播が 2 方式ある（`GraphBffEndpoints` 冒頭が正本）。

- **A) 利用者の JWT を後段へ伝播する**（`GraphBffEndpoints` / `AnalysisBffEndpoints`）
- B) BFF が解決した `AccessScope` を本文へ載せる（`SearchBffEndpoints` → RetrievalService）

**判断の軸は「後段が自分で ABAC を解決する型かどうか」**である。`WikiService` は
`IWikiAccessResolver`（`WikiAccessResolver` が `/authz/scope` を叩く）で**自分で解決する型**なので **A**。

🔴 **B を採ってはならない。** 本文で渡された scope を後段が信じる形にすると、**その経路へ到達できる
誰もが任意の scope を主張できる。** 「RetrievalService が B だから揃える」は理由にならない。

⚠️ **伝播を落とすと「全部空・全部 404」で静かに壊れる。** `WikiAccessResolver` は未認証を
`Granted=false` へ短絡させる（[[IADR-0335]] 決定 4）ため、ヘッダが届かないと一覧・検索は 200 ＋ 空、
個別は 404 になる —— **「Wiki に何も無い」と読める壊れ方**である。
BFF セッション方式（ADR-0032 / [[IADR-0251]] / [[IADR-0273]]）では
`SessionTokenPropagationMiddleware` がセッション Cookie のアクセストークンを `Authorization` へ
載せるので、中継はその結果を読むだけでよい（**新しい方式を発明しない**）。

### 決定 3: BFF 側に ABAC の前段（`BffScopeResolver`）を置かない

`GraphBffEndpoints` と同じ理由である。置いても得るものが無く、次の 3 つだけが増える。

1. 拒否が **403** になり、後段が 404 へ倒している存在秘匿と応答が割れる（[[IADR-0009]]）
2. ABAC の判断点が 2 つになり、片方が腐っても気付けない
3. 後段が必ず行う `/authz/scope` の往復が**二重になる**

**BFF に置ける門は `Granted` だけ**であり、文書条件（`AbacPageFilter`）は台帳（`WikiPage`）の行が
要るため BFF では当てられない。**後段の門がそれを包含する。**
`DashboardBffEndpoints` の多層防御（[[IADR-0044]]）と割れて見えるが、**あちらが両側に置いたのは
静的な「ロール」であって、要求ごとに解決する ABAC ではない**（[[IADR-0300]] と同じ切り分け）。

### 決定 4: 認証は必須・ロールは要求しない。未認証は **401**

群に `RequireAuthorization()` だけを付ける（契約の `x-roles: []`）。計画 `05_screens` は利用者グループ
（`SC-01`〜`SC-04`）を「**ABAC の権限内で全利用者が利用できる**」と定めており、**ロールを足すと
一般利用者が Wiki を 1 ページも開けなくなる**。可視性を決めるのは役割ではなく ABAC である。

🔴 **[[IADR-0335]] 決定 4「401 にはしない」と矛盾しない。** 同決定の逐語は
「**401 にはしない。エッジは BFF（ADR-0032 / Token Handler）であり、ここは mesh 内の後段である**」で
あり、**401 を置く場所として BFF を名指ししている**。ここで 401 を置くのはその指示どおりである。

- 未認証の要求は **BFF で止まり、後段へ到達しない**。したがって後段が固定した契約
  （一覧・検索は 200 ＋ 空、個別は 404）は**1 ミリも動かない**。
- `NFR-09` の暫定運用「エッジ（BFF）で OIDC/JWT を担保する」（#656）と、
  `check-bff-authz-docs.js` の不変条件「**`/bff/*` に無認証の端点は存在してはならない**」に従う。

**2 つの層が違う応答を返すことが食い違いなのではない。同じ層で応答が定まらないことが食い違いである**
—— [[IADR-0335]] が塞いだのは後者（ポリシーの内容次第で匿名の応答が変わる状態）である。

### 決定 5: 応答は透過する。不達は 502。既定・上限は後段だけが持つ

- 後段の **404**（権限外・不存在・アーカイブ済みを区別しない。[[IADR-0009]] / ADR-0011）を
  **そのまま返す**。403 へ変えると**権限外の文書が実在することが漏れる**。
- 後段の **200 ＋ 空**（deny-by-default）もそのまま返す。中身のある 200 へ寄せない。
- 後段の **502**（Wiki.js 不達。[[IADR-0335]] 決定 2）もそのまま返す。
  **故障を空で隠さない** —— 存在秘匿が区別させないのは「権限が無い」と「該当が無い」であって、
  「壊れている」は別の軸である。
- **BFF から後段へ到達できない場合も 502。** 空の 200 へ縮退すると「Wiki に何も無い」と読ませる。
- `q` / `limit` は**指定されたときだけ**後段のクエリへ載せる。**既定 20 / 上限 50 のクランプは
  `SearchWikiPagesEndpoint` が唯一の情報源**である（[[IADR-0346]] 決定 4 と同じ理由 ——
  2 つ持つと、後段を変えたとき BFF だけ古い上限で切る）。生のクエリ文字列も素通しにしない。

### 決定 6: named client のコード既定は `:8080`。readiness には入れない

`Services:WikiService` 未設定時の既定を `http://wiki-service:8080` とする（後発サービスの規約。
[[IADR-0089]] / #342 の「上書き漏れで 21 秒タイムアウト → 502」の面を最初から作らない）。

🔴 **helm の `Services__WikiService` は以前から在った**（`values.yaml`）。**named client が無いまま
宛先だけが先に入っていた宙ぶらりん項目**であり（[[IADR-0089]] の作業仕様書が当時そう記録している）、
本決定でようやく実体を得る。値は `http://wiki-service:8080` でコード既定と一致するため**変更しない**。
**compose 側の上書きは足さない** —— コード既定が既に `:8080` であり、compose のサービス名も
`wiki-service`（`:8080` 公開）なので `check-bff-downstreams.js` の不変条件を上書き無しで満たす
（実行して 0 件を実測した）。

**readiness の `UriHealthCheck` には入れない。** Wiki 閲覧は 1 機能であり、後段の不調で BFF 全体を
not-ready にするのは fail-safe の後退である（`McpServer` / `DocumentService` / `NotificationService` も
入っていない＝実測）。

### 決定 7: 契約に応答スキーマを置くが、C# 契約 record は作らない

`docs/api/openapi.yaml` に `WikiPageSummary` / `WikiSearchHit` / `WikiPageView` を新設して
`/bff/wiki/*` の応答から参照する（`#1200` の画面が生成フックの型を使うため）。
**`Shared.Contracts` へ record を持ち上げない** —— 形は `WikiService` の内部 record であり、
BFF は本文を**そのまま透過**するので型を通さない。持ち上げると使う側が居ないまま
`check-contract-schema.js` の baseline だけが動く。`check-openapi-dto-drift.js` は同名の C# record が
無いスキーマを対象外にする（`findDrift` の `if (!csProps) continue;`＝実測）。

**あわせてサービス直の `/wiki/pages/by-doc/{documentId}` の欠落も埋めた** ——
4 経路のうち 1 本だけが契約に載っていなかった（実測）。BFF 口だけ載って後段が載っていない状態を残さない。

## 検討した代替案

| 案 | 却下の理由 |
| --- | --- |
| platform 同居（`Platform.Bff/Foundation/Endpoints/`）へ置く | 後段が knowledge ユニットである。判定軸を「機能の見た目」に変えると次に迷う人が別の答えを出す |
| BFF が解決した scope を本文で後段へ渡す（方式 B） | 経路へ到達できる誰もが任意の scope を主張できる。後段はホップごとに自分で解決する型である |
| 読み取りに `BffScopeResolver` を通す | 403 と 404 が割れて存在秘匿が破れ、判断点が 2 つになり、`/authz/scope` の往復が二重になる |
| 未認証を後段に合わせて 200 ＋ 空 / 404 にする | `/bff/*` に無認証の端点を作ることになり `NFR-09` の暫定運用と検査器の不変条件に反する。[[IADR-0335]] 自身が 401 の置き場所として BFF を名指ししている |
| BFF でも `limit` をクランプする | 上限の正が 2 箇所になる。後段を変えたとき BFF だけ古い上限で切る |
| 404 を 403 へ変換する | 権限外の文書の実在が漏れる（存在秘匿が BFF 層で破れる） |
| 後段不達を空の 200 にする | 「Wiki に何も無い」と読ませ、権限で消えたのか壊れているのかを利用者が区別できなくなる |
| 生のクエリ文字列を素通しする | 後段の面に無いパラメータを無検査で渡す口ができる |
| readiness に足す | 1 機能の後段の不調で BFF 全体を not-ready にする（fail-safe の後退） |
| `WikiService` 側も直す | ADR-0073 §結果が「**`WikiService` の実装は変更不要である**」と明記している |

## 結果

- **`/bff/wiki/*` の 4 経路が開いた。** 合成点は **19 → 20 モジュール**になった
  （`BffEndpointCompositionTests` の件数・期待グループ集合を更新）。
- **orval 生成フックが 4 本できた**（`useBffWikiPageList` / `useBffWikiSearch` /
  `useBffWikiPageBySlug` / `useBffWikiPageByDocument`）。**`#1200`（SC-04 の画面）はこれを使う。**
- **BFF テスト 18 件**（`BffWikiEndpointTests`）が、認可の両側（未認証 401 × 4 経路 /
  一般利用者 200）・資格情報の伝播・クエリの載せ替え・404 と 502 の透過・200 ＋ 空の透過と陽性対照・
  不達の 502・上流解決を固定する。
  **変異試験で実測**: `Authorization` の転送を落とすと **6 件**が fail、`limit` の載せ替えを落とすと
  **1 件**が fail した（落としても緑のままなら、この 18 件は何も測っていないことになる）。
- **テストの置き場所**: `Platform.Bff.Tests/` は下位ディレクトリを 1 つも持たない（実測:
  `find … -type d` が自分自身 1 件だけを返す）。#1063 の `Tests/` 鏡写し移送の射程外である
  （[[IADR-0346]] §結果が同じことを実測つきで記録している）。よって既存の平置き規約に従った。
- 🔴 **残るのは画面である。** ADR-0073 §残るもの「**SC-04 の画面実装が入るまで、本番で Wiki 閲覧が
  できない**」は本作業では解けない。口は開いたが、**画面は依然として委譲先への外部リンク 1 本**である。
  「塞がっているから安全」と「機能していない」を混同しない。
- **local の直接露出は残る**（ADR-0073 決定 5。dev には ABAC の統制が無い）。
- **Wiki.js での編集の是非は未決**（ADR-0073 決定 6）。

## 関連

- Supersedes: **なし**（決定を覆したのではない）。
  **[[IADR-0335]] §結果 のフォローアップ「`/bff/wiki/*` は作らない」だけが、
  `ADR-0073` 決定 4 により解けた。** [[IADR-0335]] の決定 1〜4（検索の委譲・前段での絞り直し・
  委譲口の分離・未認証の存在秘匿）は**すべて有効なまま**である。同 IADR には日付つき追記で
  本 IADR を指した（決定文の本文は書き換えていない）。
- Issue: #1199（本決定）／#1200（SC-04 の画面。本決定の口を使う）／#1126（[[IADR-0335]] の出所）
- 作業仕様書: `.ai-context/specs/20260903_issue-1199_bff-wiki-routes.md`
