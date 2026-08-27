---
title: 二段検索の完成 — 辺型重みの公開・実重み再ランク・Authorization 伝播の残件解消
type: spec
status: in-progress
related_ids: [FR-04, FR-05, FR-17, FR-14, UC-10, ADR-0035, ADR-0034, ADR-0018, IADR-0263, IADR-0259, IADR-0242]
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0035_graphrag-retrieval-strategy.md
  - planning:projects/microservices-platform/07_adr/ADR-0034_graph-traversal-abac-enforcement.md
---

# 仕様書: 二段検索の完成 — #970 残件（重み公開・実重み化・Authorization 伝播・CALLERS）

> 本書は**着手前**に作成した。先行仕様書 `.ai-context/specs/20260823_issue-970_two-stage-graph-expansion.md`
> の §残件 1〜3（および IADR-0263 影響・残件 1〜3）を解消する後半戦である。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-04**（AI 回答と出典）/ **FR-17**（知識グラフ）/ FR-05（ABAC）/ FR-14（着脱可能な段）
- ユースケース: **UC-10**
- 関連 ADR: **ADR-0035 決定 2**（辺の型による重み付けを再ランクで使う）/ ADR-0034（権限伝播 方式 A）/ ADR-0018
- 実装 ADR: **IADR-0263**（段の設計。決定 6「重みが取れないため暫定 0.5」を本作業が解消する）

## 対象範囲（先行仕様書の残件の写像）

| # | 残件（IADR-0263 影響・残件） | 本作業 |
| --- | --- | --- |
| 1 | 辺の型の重みが GraphService のどの口からも取れない → 再ランクが全辺 0.5 | `EdgeTypeCatalogItemDto` の**末尾に既定値付き**で `Weight` を追加し、`/graph/edge-types/catalog` が返す。アダプタが辞書を引いて実重みで再ランクする |
| 2 | 呼び出し元（BFF `/bff/search`・AiAnalysisService `RagOrchestrator`）が RetrievalService へ `Authorization` を伝播していない → 段を有効化しても展開 0 件 | 両呼び出し元で受信 `Authorization` を下流へ伝播（写像元は BFF の既存作法 `FetchTagDictionaryAsync`。SearchBffEndpoints.cs:157-160） |
| 3 | `scripts/check-bff-downstreams.js` の CALLERS に RetrievalService（service→service の呼び出し元）が無い | CALLERS へ追加。パーサが導出できるよう Program.cs の named client 登録をリテラル＋インライン既定値の確立形へ揃える |

### 対象外

- SC-18 / SC-21 の画面・コミュニティ要約（ADR-0035 決定 3・5・6・7。#970 と同じ）
- `/search` の外部契約（`SearchRequest` / `SearchResponse` / `CitationDto`）は 1 バイトも変わらない
- 管理者向け `EdgeTypeDto`（使用件数つき一覧）への `Weight` 追加と SC-09 での重み編集 UI —— 再ランクが要るのは
  カタログ側だけであり、編集機能は計画（SC-09）に無い。**計画外の機能追加をしない**
- `docs/api/openapi.yaml` —— 生成物であり手で書き足さない（CI が更新する）

## 母集合（是正・追随の対象を、着手前に自分で引いた。規則 1〜10）

`.git` / `obj` / `bin` / `node_modules` のみ除外し、拡張子で絞らず全追跡ファイルを走査した（2026-08-28 実測）。

| 軸 | 検索語（あり得る形を列挙） | ヒット | 処置 |
| --- | --- | --- | --- |
| 1 | `UnavailableEdgeWeight` | 1 ファイル（`GraphServiceNeighborExpander.cs`） | 実重み化で書き換え（フォールバック定数として残す） |
| 2 | `EdgeTypeCatalogItemDto` | 11 ファイル | 下表 |
| 3 | `どの口からも` / `全辺 0.5` / `全辺を既定重み` / `重みが公開され` | 3 ファイル（アダプタ・先行仕様書・IADR-0263） | アダプタは書き換え。凍結記録は日付つき追記（下記） |
| 4 | `まだ誰も使わない` / `現時点では未使用` | 5 ファイル（`EdgeType.cs`・`EdgeTypeSeed.cs`・`EdgeWeightAndHubDegreeTests.cs`・947a 仕様書・970 先行仕様書） | コード 3 件は「使われている」へ追随。仕様書 2 件は凍結記録 —— 「使うのは #970」という予告は本着地で真になる（記述は誤りにならない）ため触らない |
| 5 | `伝播していない` / `PostAsJsonAsync のみ` | 5 ファイル | 実装 0 件（記録のみ）。IADR-0263 残件 2 は日付つき追記で解消を記す。`scripts.repo.test.js`・`20260817_issue-836` は別件の同語（Authorization と無関係）で対象外 |
| 6 | `check-bff-downstreams` | 27 ファイル | 変更は `scripts/check-bff-downstreams.js` 本体のみ。`scripts/README.md` は挙動一覧 —— CALLERS の顔ぶれを列挙していないことを確認済み（追随不要）。他は凍結記録・別 issue の記録 |
| 7 | `重みの項目が無い` / `重みを持たない` / `重みが取れない` / `重みが無い` | 3 ファイル（軸 3 と同じ） | 同上 |

- **除外したものと理由**: `CHANGELOG.md`（生成物）・`scripts/contract-schema-baseline.json`（軸 2 でヒット。
  **本作業では更新しない** —— 契約追加はスナップショット差分として exit 1 になるが、baseline 更新は
  レビュー対象として PR 側で行う統制のため。検査結果は §検証 に記録する）・`.ai-context/` の確定済み
  仕様書（凍結。上表のとおり記述は誤りにならない）。
- 規則 10（この変更で新たに誤りになる自分の記述）: 「重みが取れない」「伝播していない」系の記述が
  すべて誤りになる。軸 1・3・4・5・7 がそれを引いており、live なコード側は全件書き換える。
  IADR-0263（live な権威文書）は決定 6・残件 1〜3 に `［2026-08-28 追記 / #970］` を置き `updated:` を前進させる。
  先行仕様書（in-progress）は残件節へ同書式の追記を置き status を done へ進める。

## 設計

### 1. 辺型の重みの公開 —— カタログ（`/graph/edge-types/catalog`）に載せる

- **Domain・DB は変更しない。** `EdgeType.Weight` は #947a が実装済み（列・migration
  `20260822092002_AddEdgeTypeWeight` とも着地済み）。**公開面だけが欠けていた。**
- `EdgeTypeCatalogItemDto` の**末尾に既定値付き**で `double Weight = 0.5` を足す
  （既定値の無いメンバー追加は破壊的変更。IADR-0122 決定 2。既定値 0.5 は `EdgeType.DefaultWeight` と
  同値だが、契約プロジェクトはサービス実装を参照できないためリテラルで持つ）。
- **載せる口はカタログ（認証のみ・ロール不問）である。** 理由:
  - RetrievalService は一般利用者の JWT を伝播して呼ぶ（方式 A）。admin / operator 限定の
    `/graph/edge-types` は一般利用者のトークンで 403 になり、使えない。
  - 重みは型ごとの**語彙レベルの設定値**であり、`UsageCount` と違って ABAC で絞るべき集計値ではない
    （権限外文書の存在を漏らす経路にならない）。カタログの「件数を持たない」設計判断は崩さない。
- `GraphEdgeDto` / `GraphEdgeItemDto`（近傍応答の辺）には**載せない** —— 辺は `EdgeTypeId` を持ち、
  重みは型の属性である。辺ごとに複写すると改名追随と同じ理由で真実源が割れる（IADR-0263 検討 E と同型）。

### 2. アダプタの実重み化 —— 近傍探索と同じ要求文脈でカタログを 1 回引く

- `GraphServiceNeighborExpander.ExpandAsync` が、近傍探索と同じ named client・同じ伝播済み
  `Authorization` で `/graph/edge-types/catalog` を **1 要求につき 1 回**取得し、
  `EdgeTypeId → Weight` の対応で辺の重みを解決する。
- 近傍応答の辺から `EdgeTypeId` を読む（`GraphEdgePayload` へ項目追加。応答には #916a から
  既に載っている）。
- **キャッシュは持たない。** 既存のカタログ消費者（BFF の `/bff/graph/edge-types` プロキシ）も
  無キャッシュの都度取得であり、確立慣行に合わせる。展開が有効な検索でのみ +1 呼び出しで、
  辞書は型の数件〜十数件。測らずに最適化しない。
- **フォールバック**: カタログ取得の失敗（不達・非 2xx）と辞書に無い型は既定重み 0.5
  （`FallbackEdgeWeight`。旧 `UnavailableEdgeWeight` の改名）へ縮退し、**警告ログを出す**
  （静かに無差別へ落ちない —— IADR-0263 決定 6 の作法を縮退時に限って残す）。
  失敗しても検索そのものは落とさない（既存の縮退方針と同じ）。

### 3. Authorization 伝播（呼び出し元 2 箇所）

- **BFF `/bff/search`**: 受信 `Authorization` を RetrievalService へ伝播する。写像元は同ファイルの
  `FetchTagDictionaryAsync`（既存作法）。`/bff/attribute-values` → `/search/attribute-values` は
  **対象外** —— その下流路にグラフ展開は無く、挙動が変わらない伝播を測るテストが書けない
  （黙って除外しない。ここに理由を残す）。
- **AiAnalysisService `RagOrchestrator.SearchAsync`**: `IHttpContextAccessor` から受信
  `Authorization` を読み、あれば RetrievalService への要求へ載せる。無ければ**付けない**
  （縮退の判断は RetrievalService 側の既存実装が持つ。二重に持たない）。
  - コンストラクタは `IHttpContextAccessor? accessor = null` の追加引数とする。DI には
    `AddHttpContextAccessor()` を登録して解決させ、既存テストの直接構築
    （`new RagOrchestrator(factory)` 3 ファイル）を壊さない。
  - AuthorizationService / LlmGateway への呼び出しには**載せない** —— 前者は
    userId / 属性を本文で受ける既存契約（方式 B 相当が確立済み）、後者は利用者権限と無関係。
    伝播先は「自分で ABAC を解決する下流」（#916a の判断規則）だけである。

### 4. check-bff-downstreams の CALLERS 追加

- `CALLERS` へ `{ label: 'RetrievalService', program: <RetrievalService の Program.cs>, compose: 'retrieval-service', helm: 'retrieval' }` を追加。
- パーサ（`parseProgramDefaults`）は `AddHttpClient("<名前リテラル>", c => … ?? "<既定 URL>")` の形を
  要求するため、Program.cs の GraphService client 登録を確立形
  （`AddHttpClient("GraphService", c => c.BaseAddress = new Uri(builder.Configuration["Services:GraphService"] ?? "http://graph-service:8080"))`）へ揃える。
  `LlmGatewayEmbeddingService` は型付きクライアント（名前リテラル無し）でパーサ対象外（既存どおり）。
- **S6（helm / compose）は変更不要（実測）**: コード既定 `http://graph-service:8080` は、compose の
  `graph-service`（expose 8080）とも helm の chart キー `graph`（Service 名 `{{ $name }}-service` =
  `graph-service`・port 8080）とも一致し、上書き無しで実効ポート 8080 を満たす。
  compose の `retrieval-service.depends_on` に `graph-service` は無いが、段は既定オフ・
  アダプタは不達を空縮退するため起動順の強制は要らない（S6 は触らない）。

## 受け入れ基準

- [ ] `/graph/edge-types/catalog` が型ごとの `weight` を返す（seed 値 `supersedes`=1.0 / `related`=0.3 が API から読める）
- [ ] カタログは引き続き `usageCount` を返さない（既存テストが緑のまま）
- [ ] 再ランクで辺の型の重み差が順位に効く（重い型経由の文書が軽い型経由より上位）—— 実 API 応答形（FakeGraphHandler）経由で測る
- [ ] 辞書に無い型・カタログ不達は既定重み 0.5 へ縮退し、警告が出る（静かに無差別にしない）
- [ ] BFF `/bff/search` が受信 `Authorization` をそのまま RetrievalService へ伝播する／無ければ付けない
- [ ] `RagOrchestrator` が受信 `Authorization` をそのまま RetrievalService へ伝播する／無ければ付けない
- [ ] `node scripts/check-bff-downstreams.js` が RetrievalService → GraphService を含めて緑
- [ ] 既存 14 Fact（`GraphExpansionTwoStageSearchTests`）が緑のまま（段の既定オフ・Score 不混入等の既決事項を壊さない）

## テスト方針

| ID | 内容 | 置き場所 |
| --- | --- | --- |
| W-01 | カタログ応答が seed の重み（supersedes 1.0 / related 0.3）を運ぶ。**リテラルで測る**（定数との突合はトートロジー） | GraphService.Api.Tests |
| W-02 | カタログ応答の重み差が装置の検出力を持つ（supersedes > related） | 同上（W-01 に含める） |
| R-01 | 重い型（1.0）経由の近傍が軽い型（0.3）経由より上位に並ぶ（エンドポイント経由・FakeGraphHandler がカタログも応答） | RetrievalService.Api.Tests |
| R-02 | 辞書に無い型の辺は既定重み 0.5 で扱う | 同上 |
| R-03 | カタログ不達（非 2xx / 例外）でも検索は成立し、全辺 0.5 で縮退する | 同上 |
| A-01 | `RagOrchestrator`: 受信 `Authorization` が RetrievalService への要求へそのまま載る（陽性対照） | AiAnalysisService.Api.Tests |
| A-02 | 同（否定形）: 受信ヘッダが無ければ下流要求にも付かない | 同上 |
| B-01 | `/bff/search`: 受信 `Authorization` が RetrievalService への要求へそのまま載る | Platform.Bff.Tests |
| B-02 | 同（否定形）: 無ければ付かない | 同上 |

新規テストは 3 プロジェクトとも xUnit1051 migrated のため `TestContext.Current.CancellationToken` を必須とする
（Platform.Bff.Tests は未移行だが既存作法に従う）。

### 変異試験（最低 2 種・実測して本節へ記録する）

| 変異 | 落ちるべき | 実測結果 |
| --- | --- | --- |
| M-1 アダプタの重み解決を固定値 0.5 へ戻す | R-01 | （実測後に記入） |
| M-2 `RagOrchestrator` の Authorization 伝播を外す | A-01 | （実測後に記入） |
| M-3 `/bff/search` の Authorization 伝播を外す | B-01 | （実測後に記入） |

## 計画書との差異

- 差異なし。ADR-0035 決定 2「型ごとに重みを持たせ、再ランクで使う」が本作業で実際に効き始める
  （IADR-0263 決定 6 の「テストは緑・本番は無差別」状態の解消）。

## 検証（コミット前・実測を記録）

（実施後に記入: build / format / 対象 3 テストプロジェクト件数 / check-contract-schema /
check-bff-downstreams / check-commit-messages / check-event-topology）

## 未決事項・残件

1. `contract-schema-baseline.json` の更新（`EdgeTypeCatalogItemDto` への非破壊追加ぶん）は本 worktree では行わない
   （統括のレビュー統制。検査は exit 1 を報告する）。
2. `GraphExpansion:Enabled` は引き続き**既定 false**（本作業は既定を変えない。ADR-0035 決定 2）。
   有効化条件: 構成 `GraphExpansion:Enabled=true` ＋ 呼び出し元からの `Authorization` 伝播（本作業で充足）。
3. `w_graph` / `SeedCount` の実測是正（A/B）は従前どおり別途（IADR-0263 残件 5）。
