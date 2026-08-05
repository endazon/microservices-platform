---
title: SC-01〜03 の画面要素 7 点（要素名としては 6 種）が現在の API 契約に載らない（対象範囲フィルタ・機密区分チップ・検索モード・並び順・更新日時・機密区分の表示名）
type: plan-feedback
status: open
category: UC/画面の差異
related_ids: [SC-01, SC-02, SC-03, UC-01, FR-03, FR-04, FR-05]
source_repo: microservices-platform
source_ref: "feat/SC-01-03-search-flow / docs/specs/20260804_issue-502_sc01-03-search-flow.md（#502）"
author: Claude（実装）
created: 2026-08-04
updated: 2026-08-05
---

# フィードバック: SC-01〜03 の画面要素 7 点（要素名としては 6 種）が現在の API 契約に載らない

## 起票状況（**計画リポジトリへ起票済み・裁定待ち**）

`feedback/README.md` の手順は 3 段（1. `/plan-feedback` 実行 → 2. 記録作成 → 3. **計画リポへの伝達**）である。

| 手順 | 状態 |
| --- | --- |
| 2. `feedback/` への記録作成 | **完了**（本ファイル） |
| 3-a. `planning/draft/feedback/` へのコピー | **本件は実施しない**（手順 3 は[記録ファイル経路と GitHub Issue 経路の**両経路に対応**](README.md)し、[`docs/README.md` 運用ルール 5](../docs/README.md) も「計画リポへのコピー、**または** Issue 起票」と定める。本件は 3-b の Issue 経路を採ったため 3-a は行わない。**記録ファイル経路が適する場面（Issue を起票できない環境）まで否定するものではない**） |
| 3-b. `endazon/project-planning` への Issue 起票 | **完了**: [planning#197](https://github.com/endazon/project-planning/issues/197)（2026-08-05・**裁定待ち**） |
| 付-1（OpenAPI 欠落）の実装側起票 | **完了**: [#506](https://github.com/endazon/microservices-platform/issues/506)（計画の裁定は不要・実装側で閉じる） |
| 付-2（左ナビの SC-03）の起票 | **完了**: planning#197 §付随の論点 に含めた（計画の裁定が要るため） |
| §提案 5（裁定までの暫定注記）の伝達 | **完了**: planning#197 へのコメントで補った（起票本文から落ちていたのをクロス監査が検出） |

**渡し漏れは記録全体の突合で確かめる。** 本文表 #1〜#7・§#3 補足・§提案 1〜5 と 4'・§影響範囲・付-1・付-2 の
すべてが上表のいずれかへ渡っていることを、issue 本文と機械照合して確認した（提案 5 のみ漏れており、
コメントで補った）。**「起票した」は「全部渡した」を意味しない。**

フロントマターの `status: open` は TEMPLATE の既定値であり、「未起票」と「起票済み・未トリアージ」を
区別しない。**本節の表が実態の正である。**

## 種別

UC/画面の差異（**計画は画面要素を定めているが、それを支えるバックエンド契約が存在しない**）。

## 起点となる計画書

- 機能要求（FR）: FR-03（ハイブリッド検索）・FR-04（根拠付き AI 回答）・FR-05（ABAC）
- ユースケース（UC）: UC-01（検索・質問する）
- 画面（SC）: **SC-01**（検索／チャット質問）・**SC-02**（検索結果一覧）・**SC-03**（文書詳細）
- 関連 ADR: ADR-0031（フロントエンドスタック）／実装側 [[IADR-0119]]（FR-17〜21 の着手保留。本件とは別論点）
- 計画書リンク: `projects/microservices-platform/05_screens/01_screens.md` §SC-01 / §SC-02 / §SC-03、
  `05_screens/mockups/hi-fi/sc-01.html` / `sc-02.html` / `sc-03.html`（planning `d980a01`）

## 現状（計画書の記述 / As-Is）

| # | 画面 | 計画の記述 | 現在の API 契約（実測） |
| --- | --- | --- | --- |
| 1 | SC-01 | §入力/バリデーション「**対象範囲フィルタ**｜任意｜選択｜**権限内のタグ／フォルダのみ選択可**」。hi-fi はチップ（タグ: 経理／フォルダ: /規程／＋絞り込み）で描く | `POST /bff/analysis/ask/stream` の要求は `AnalysisRequest(Question, Scope?)` のみ。**属性フィルタを取らない** |
| 2 | SC-01 | 同上（**候補は権限内のみ**） | 一般利用者が呼べる**タグ辞書・フォルダ一覧の BFF エンドポイントが無い**（タグ／属性の辞書は `/bff/admin/authz`＝管理者限定） |
| 3 | SC-02 | §主要素「**検索モード切替（キーワード｜意味）**」 | `SearchRequest(Query, TopK, AttributeFilters, Scope)` にモードが無い。RetrievalService は**常にハイブリッド**（語彙＋ベクトル）で、片方だけに切り替える経路が無い |
| 4 | SC-02 | §主要素「**並び順（関連度ほか）**」 | 同上。並び順パラメータが無く、応答は関連度（`Score`）降順のみ。**「ほか」が何かも計画に無い** |
| 5 | SC-02 | §主要素「結果テーブル（文書／タグ／**更新日時**、スニペット抜粋付き）」 | `SearchResultDto(ChunkId, DocumentId, DocumentTitle, Text, Score, MarkdownUri, Attributes, Tags)` に**日時が無い** |
| 6 | SC-03 | §主要素「属性・タグパネル（**機密区分**・部門・タグ）」。hi-fi は `internal` を「社内限」、SC-05 / SC-09 は `confidential` を「秘」と描く | 値集合は `public` / `internal` / `confidential` / `restricted` の **4 値**（06_technical/07_abac-attribute-model）。**モックに現れるのは 2 値の表示名だけ**で、`public` / `restricted` の表示名は計画のどこにも無い |
| 7 | SC-01 | hi-fi `sc-01.html:428-429` は**出典 1 行に 2 つのチップ**を描く——`社内限`（機密区分）と `組織文書`（種別） | `CitationDto(Number, DocumentId, DocumentTitle, ChunkId, SourceUri, Score, Snippet)` に**属性フィールドが無く、機密区分を出せない**。#502 は `組織文書` のみ実装した |

> **#6 と #7 は別の論点である。** #6 は「値の**表示名**が計画に無い」（項目は API から取れる）。
> #7 は「**項目そのもの**が API に無い」（表示名の議論以前に、画面が値を持っていない）。
> 混同すると #7 が「表示名を決めれば済む」ように読めてしまう。

**実測の出所**（対象コミット `83ff0fd`・planning pin `d980a01`）:

- `src/knowledge/backend/Shared/Knowledge.Contracts/Dtos/SearchDto.cs`（`SearchRequest` / `SearchResponse`）
- `src/knowledge/backend/Shared/Knowledge.Contracts/Dtos/SearchResultDto.cs`（`SearchResultDto` / `CitationDto`）
- `src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/{SearchBffEndpoints,AnalysisBffEndpoints}.cs`
- BFF のルートグループ全 10 件: `grep -rn 'MapGroup("/bff' src/platform/backend src/knowledge/backend`
  → `/bff/admin/authz`・`/bff/admin/config`・`/bff/analysis`・`/bff/conversion/jobs`・`/bff/dashboard`・
  `/bff/datasources`・`/bff/documents`（読み取り／書き込みの 2 グループ）・`/bff/feedback`・`/bff/search`
- 表示名の実測: `grep -n "社内限\|秘" planning/.../05_screens/mockups/hi-fi/sc-05.html sc-09.html`

### #3（検索モード）についての補足 — **必要なのは契約の追加であって、検索能力の新規実装ではない**

裁定コストを過大に見積もらせないために、一次情報を添える。

`IVectorStore`（`.../RetrievalService.Api/Foundation/Ports/IVectorStore.cs:11,19`）は
**`SearchAsync`（ベクトル）と `KeywordSearchAsync`（語彙）を別のメソッドとして既に持ち**、
`InMemoryVectorStore`（`Composable/Adapters/InMemoryVectorStore.cs:12,26`）は両方を実装している。
すなわち**片方だけを実行する能力は既にある**。無いのは
「どちらで検索するか」を**呼び出し側から指定する経路**（`SearchRequest` のパラメータと、
それを `HybridSearchService` へ通す配線）だけである。

同様に #4（並び順）も、応答は既に関連度（`Score`）降順で確定しており、無いのは**指定の口**である。
一方 **#5（更新日時）は本当に無い**——索引（Qdrant のペイロード）へ日時を取り込むところから要る。
**3 件は必要な作業量が同じではない。**

## 問題点 / あるべき姿（To-Be）

**画面だけ作っても機能しない。** 1〜5 は「押しても結果が変わらない操作」「常に空の列」を生む。
6 は実装が表示名を決めると、それが**事実上の用語定義**になってしまう（機密区分は取り違えの影響が大きい）。
7 は API が値を持たないため、実装が出せるとすれば**推測した固定値**しかない
——機密区分の推測表示は、取り違えの影響が最も大きい種類の誤りである。

#502 では 1〜5・7 を**実装せず**（6 はキーのみ写像し値は生値で表示）、画面仕様書へ「実装しない要素と理由」として明記した
（`docs/screens/SC-01_search-chat.md` §hi-fi モックアップとの対応 ／ `SC-02_search-results.md` 同 ／
`SC-03_document-detail.md` §属性の表示）。**繰り延べであって放棄ではない。**

あるべき姿は次のいずれかである（計画側の裁定を仰ぐ）。

- (A) 契約を拡張する。FR-03 に「検索モード・並び順の指定」「結果に更新日時を含める」を、
  FR-04 に「対象範囲の指定」を要求として明示し、実装側で API を拡張する。
- (B) 画面要素を落とす。モックの当該要素を削り、計画本文からも外す。
- (C) 保留する。要求としては残しつつ、実装の着手条件（対応 API の実装）を明記する。

## 実装で判明した経緯

#502（SC-01〜03 の新スタックでの再実装）で、hi-fi モックアップの要素を 1 つずつ実装可能性へ写像する過程で判明した。
旧実装（削除済み）はこれらの要素を**そもそも作っていなかった**ため、差異が表面化していなかった。
再実装にあたり「モックを正として全要素を突き合わせる」作法を採ったことで顕在化した。

## 提案（計画への反映案）

- 反映先候補: **要求更新（FR-03 / FR-04）** ＋ **画面更新（SC-01 / SC-02）** ＋ **用語追加（機密区分の表示名）**
- 提案内容:
  1. **FR-03 へ検索の指定軸を明示する**（モード＝キーワード／意味／ハイブリッド、並び順＝関連度・更新日時ほか）。
     「ほか」を具体化しないと実装が推測することになる。
  2. **検索結果に更新日時を含める**ことを FR-03 の受け入れ基準へ加える（索引側の取り込みを伴う）。
  3. **FR-04（または SC-01）へ「対象範囲フィルタ」を支える 2 つの API を明示する**——
     (i) AI 回答要求への属性フィルタの付与、(ii) **権限内**のタグ／フォルダ候補の取得。
     とくに (ii) は「権限内のみ提示」という計画の保証を実現する唯一の手段であり、
     ABAC のスコープ解決結果を利用者へ返す API（存在秘匿との整合が要る）になる。
  4. **機密区分 4 値の表示名を用語集（`docs/glossary.md`）または 07_abac-attribute-model へ定める**
     （`public` / `internal` / `confidential` / `restricted`）。モックの「社内限」「秘」を正とするなら、
     残る 2 値の表示名も同じ場所に置く。
  4'. **出典（`CitationDto`）へ機密区分を含めるかを定める**（表 #7）。含めるなら DTO の項目追加であり、
     4 の表示名の議論はその後に効く。含めないなら hi-fi `sc-01.html:428-429` の `社内限` チップを
     モックから外す。**「出典の機密区分を利用者に見せるか」自体が判断事項である**
     （見せると、権限内であることは前提でも、文書の機微度が回答画面に露出する）。
  5. 上記が確定するまでは、SC-01 / SC-02 の当該要素に「**対応 API 未実装のため画面も未実装**」の注記を置く。

## 付随して判明した 2 件（本体とは別論点。同じ記録に載せる）

### 付-1. `docs/api/openapi.yaml` に SC-03 / SC-01 が使う BFF が 1 行も無い

**実測**（`grep -n "^  /bff" docs/api/openapi.yaml`。対象コミット `83ff0fd`）: 記載があるのは
`/bff/search`・`/bff/analysis/ask`・`/bff/analysis/analyze`・`/bff/feedback`・`/bff/feedback/stats`・
`/bff/dashboard/summary`・`/bff/admin/config`・`/bff/admin/config/drift` の **8 パス**である。
`grep -c "bff/documents\|ask/stream" docs/api/openapi.yaml` は **0** であり、
**`/bff/documents/{id}`・`/content`・`/versions`（SC-03 の全データ源）と
`/bff/analysis/ask/stream`（SC-01 の回答）は載っていない。**

これは実装側の規約に直接ぶつかる。`CLAUDE.md` は「呼び出しは **orval 生成フック**
（入力は `docs/api/openapi.yaml` の `/bff/` 配下のみ）か `foundation/api` の `apiFetch` / `apiStream`」と
定めており、**OpenAPI に無いエンドポイントには生成フックが存在しない**。したがって SC-03 は
`apiFetch` ＋ 手書きの TypeScript 型で書くしかなく、**BFF の DTO が変わっても型検査は素通りする**
（#502 はこの形で実装した。規約違反ではないが、契約検査の網が掛からない）。

`ask/stream` は SSE であり orval が扱えないため生成対象外なのは妥当だが、
**`/bff/documents/*` は素直な JSON API であり、載っていない理由が無い。**

- 提案: `docs/api/openapi.yaml` へ `/bff/documents/{id}`・`/content`・`/versions` を追加し、
  SC-03 を orval 生成フックへ載せ替える（後続 issue）。SSE は対象外である旨を明記する。
- 反映先候補: 実装側の後続 issue（計画の変更は不要と思われる）。**[#506](https://github.com/endazon/microservices-platform/issues/506) として起票済み**（§起票状況）。

### 付-2. 計画は左ナビに SC-03（文書詳細）を置いているが、実装は置いていない

**計画の記述**: `05_screens/01_screens.md:110` §共通シェル の左ナビ 4 グループは、
「利用者」に **SC-01 検索・質問／SC-02 結果一覧／SC-03 文書詳細／SC-04 Wiki閲覧／…** を列挙している。
hi-fi モックの左レール（`sc-01.html:414` ほか）にも「文書詳細」がある。

**実装は置いていない**（#502。旧実装からの継続で、本 issue で変えていない）。
理由は技術的なものである——SC-03 のルートは文書 ID を必須とし（`/docs/$id`）、
**ID を持たないナビ項目からは到達できない**（`/docs/` は未知パスとして 404 になる）。
モックのリンクは画面間の遷移例を示すために全画面へ張られており、
ナビ項目として機能する URL を示しているわけではない。

- 論点: 計画のグループ分けは「画面の**所属**」を示すものか、「各画面が**単独のナビ入口を持つ**」ことまで
  要求するものか。後者なら、ID を持たない入口（例: 直近に見た文書／文書一覧）の仕様が要る。
  なお ID を持たない入口は SC-02（結果一覧）が既に担っている。
- 反映先候補: **画面更新（05_screens §共通シェル に注記）**。実装は裁定まで変更しない。

## 影響範囲

- **実装**: RetrievalService（検索モード・並び順・更新日時）／ IngestionService（索引への更新日時の取り込み）／
  AuthorizationService ＋ BFF（権限内候補の取得 API）／ `Knowledge.Contracts`（DTO 変更。
  実装側の契約互換ゲート `scripts/check-contract-schema.js` の対象）。
- **画面**: SC-01（フィルタ）・SC-02（モード・並び順・更新列）・SC-08（AI 分析の範囲指定と同型の論点）。
- **存在秘匿**: 提案 3(ii) は「利用者が到達できる属性値の集合」を返すため、
  [ADR-0004](../planning/projects/microservices-platform/07_adr/ADR-0004_authz-abac.md) の 404 原則との整合が要る
  （文書の存在は示さないが、属性値の存在は示す）。**この点は ADR の判断事項である。**
