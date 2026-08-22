---
title: ハイブリッド検索 テスト仕様書
type: test-spec
status: in-progress
created: 2026-07-04
updated: 2026-08-23
author: claude
---
<!-- trace:
ids: [FR-03, SC-01, SC-02, UC-01]
adrs: [ADR-0016]
iadrs: [IADR-0014, IADR-0131, IADR-0149, IADR-0150, IADR-0256]
specs: [20260823_issue-995_bff-search-500]
issues: [#532, #536, #642, #995]
-->

# テスト仕様書: ハイブリッド検索

## 起点となる計画書（トレーサビリティ）

- 機能要求: キーワードと自然文の双方による横断検索（ベクトル＋全文のハイブリッド）
- ユースケース: 検索・質問する
- 受け入れ基準の所在（02_requirements）: `02_requirements/01_requirements.md`
- 計画書リンク: 同上 / `07_adr/ADR-0009`（Qdrant ベクトルDB）

## テスト対象・範囲

- 対象: RRF 融合ロジック（`HybridSearchService.ReciprocalRankFusion`）、`/search` エンドポイント結合（`InMemoryVectorStore`）、ABAC フィルタ適用（両系統）、
  Qdrant ペイロードからの ABAC 属性復元（`QdrantVectorStore.ExtractAttributes`）、**Qdrant ペイロードへのタグ書き込みと復元（`QdrantVectorStore.BuildPayload` / `QdrantVectorStore.ExtractTags`。［2026-08-09 追記 / #642］）**。
- 対象: 上記に加え、**クエリ埋め込みが得られないときの縮退と、後段の故障を隠さないこと**（`HybridSearchService`）。
- 対象外: 実 Qdrant の full-text Match 挙動・実埋め込みモデルの精度、反映時間（インジェスト責務）、負荷/p95、画面。
- 🔴 **`InMemoryVectorStore` は `queryVector` を参照しない**ため、「0 次元ベクトルを渡すと実機が失敗する」ことは
  **結合テストの応答からは観測できない**。観測できるのは「ベクトル系統を呼んだかどうか」だけである（T-37/T-38）。

## テスト観点

- 正常系: ベクトル＋全文の融合、両系統出現文書の上位化、片系統のみの取りこぼし防止、融合スコアの降順、出典（タイトル/URI）付与。
- 境界/異常系: 空クエリ・両系統 0 件・属性欠落文書の扱い。
- セキュリティ（ABAC）: 単値フィルタ・多値 allow-list による除外、`GrantsAccess=false`・`Scope` 未指定の fail-closed、ペイロードからの属性復元（フラット／ネスト）。

## テストケース一覧

| ID | 前提条件 | 手順 | 期待結果 | 対応受け入れ基準 | 区分（自動/手動） |
| --- | --- | --- | --- | --- | --- |
| T-01 | 両系統に出現する文書＋片方のみの文書 | `ReciprocalRankFusion(vector, keyword)` | 両リストに出現する文書が最上位、片方のみの文書も結果に含まれる | RRF 統合・上位化 | 自動 |
| T-02 | 各リストに 1 件ずつ別文書 | `ReciprocalRankFusion(a, b)` | 2 件とも結果に含まれる（取りこぼさない） | RRF 統合 | 自動 |
| T-03 | 両系統上位に同一文書 | `ReciprocalRankFusion` | 融合スコアが降順に並び、最上位スコアが `2/61`（≒0.0328）に一致 | RRF 統合 | 自動 |
| T-04 | 空リスト×2 | `ReciprocalRankFusion([],[])` | 空結果 | 例外フロー | 自動 |
| T-05 | 対象＋無関係の 3 チャンクを Seed、`Scope`（空フィルタ, `GrantsAccess=true`） | `POST /search`（Query="アルファ", TopK=10） | 200・キーワード一致文書が最上位・`DocumentTitle`/`MarkdownUri` が非空（出典付き） | 横断検索・出典 | 自動 |
| T-06 | `dept=sales`/`dept=hr` の 2 文書、`AttributeFilters={dept:sales}`＋許可 Scope | `POST /search` | `sales` 文書は含まれ、`hr` 文書は現れない | 権限制御（単値） | 自動 |
| T-07 | `confidentiality` が public/internal/confidential の 3 文書、`Scope` 許可値={public,internal} | `POST /search` | public・internal は含まれ、confidential は現れない | 権限制御（多値 allow-list） | 自動 |
| T-08 | スコープ属性を持つ文書と持たない文書、`Scope` 許可値={internal} | `POST /search` | 属性付き文書のみ含まれ、属性欠落文書は除外（deny-by-default） | 権限制御 | 自動 |
| T-09 | 文書を Seed、`Scope`（`GrantsAccess=false`） | `POST /search` | 200・空結果（許可ポリシー無し） | fail-closed | 自動 |
| T-10 | クエリ一致文書を Seed、`Scope` 未指定（null） | `POST /search` | 200・空結果（Scope 未解決を全件返却しない） | fail-closed | 自動 |
| T-11 | 文書を Seed、`Query`="" | `POST /search`（TopK=5） | 200・空結果 | 例外フロー | 自動 |
| T-12 | フラットキー `attributes.{k}` を持つペイロード | `QdrantVectorStore.ExtractAttributes` | `confidentiality`/`department` を復元（2 件） | 権限制御の前提（属性復元） | 自動 |
| T-13 | ネスト構造体 `attributes` を持つペイロード | 同上 | ネストから属性を復元 | 権限制御の前提（属性復元） | 自動 |
| T-14 | 属性を持たないペイロード | 同上 | 空辞書を返す | 権限制御の前提（属性復元） | 自動 |
| T-15 | フラットキーとネストで同一キーが競合 | 同上 | フラットキー値を尊重（ネストで上書きしない） | 権限制御の前提（属性復元） | 自動 |
| T-16 | `updated_at`（epoch ミリ秒）を持つペイロード | `QdrantVectorStore.ExtractUpdatedAt` | 元の `DateTimeOffset` を復元する | 検索結果一覧の裁定 Q6 | 自動 |
| T-17 | `updated_at` を**持たない**ペイロード（未再索引） | 同上 | **`null`**（`0001-01-01` で埋めない。更新日時の索引表現の実装判断による） | 検索結果一覧の裁定 Q6 | 自動 |
| T-18 | `updated_at` が整数でない（文字列で書かれた混入） | 同上 | **`null`**（黙って誤った日時にしない） | 縮退 | 自動 |
| T-19 | 両系統に同一チャンク（更新日時あり） | `ReciprocalRankFusion` | 融合後も **`UpdatedAt` が残る**（RRF はスコアだけを差し替える） | 検索結果一覧の裁定 Q6 | 自動 |
| T-20 | 片方だけ日時を持つ（再索引済みと未再索引の混在） | 同上 | 再索引済みは値を保ち、未再索引は `null` のまま | 縮退 | 自動 |
| T-21 | 後段が `updatedAt` を返す | `POST /bff/search` | **BFF が欠落させずに透過**する（`BffSearchEndpointTests`） | 検索結果一覧の裁定 Q6 | 自動 |
| T-22 | `SortBy` 未指定 | `SearchAsync` | **取得順（関連度順）のまま**＝現行の振る舞いを変えない | 検索結果一覧の裁定 Q5 | 自動 |
| T-23 | `SortBy = updated` | 同上 | 更新日時の**降順**になる | 検索結果一覧の裁定 Q5 | 自動 |
| T-24 | 日時を持たないチャンクが混ざる | 同上 | **末尾へ置く**（`MinValue` 扱いにしない。並び順は取得後に適用するという実装判断による） | 縮退 | 自動 |
| T-25 | 同着（同じ日時・日時なし同士） | 同上 | **元の順序＝関連度の順**を保つ（安定ソート） | 並び順の実装判断 | 自動 |
| T-26 | 未知値・空・`null` の `SortBy` | 同上 | 既定（relevance）へ縮退する（大文字小文字も問わない） | 契約で `enum` にしない方針 | 自動 |
| T-27 | `SortBy = updated` かつ単系統 | 同上 | **候補を `topK*4` まで広げる**（関連度順では広げない）。並び順の実装判断による | —| 自動 |
| T-28 | `SortBy = updated` で候補を広げた | 同上 | **返すのは `topK` 件**（広げた分をそのまま返さない） | —| 自動 |
| T-29 | 画面が `sortBy` を送る | `POST /bff/search` | **BFF は縮退させず後段へそのまま渡す**（正規化は RetrievalService の 1 か所。`BffSearchEndpointTests`） | —| 自動 |
| T-30 | `tags` を持つペイロード（`ListValue` の文字列） | `QdrantVectorStore.ExtractTags` | `["経理","規程"]` を順序どおり復元 | 結果一覧のタグ表示（#642） | 自動 |
| T-31 | `tags` を持たないペイロード | 同上 | 空リスト（画面はタグ列を空欄にする） | 同上 | 自動 |
| T-32 | `tags` がリストでない（手投入・旧データ） | 同上 | 空リスト（検索全体を失敗させない） | 例外フロー | 自動 |
| T-33 | `tags` に数値・真偽値・構造体が混在 | 同上 | スカラーは文字列化（`"42"`/`"true"`）、構造体は読み飛ばす | 例外フロー | 自動 |
| T-34 | タグを持つ `ChunkPayload` | `QdrantVectorStore.BuildPayload` | `payload["tags"]` が `ListValue[StringValue]`（**取り込み側 `QdrantIngestionVectorStore.BuildChunkPayload` と同じ表現**） | 表現の一致（属性ペイロードの表現統一・#642） | 自動 |
| T-35 | タグ 0 件の `ChunkPayload` | 同上 | `tags` キー自体を書かない（`attributes` と同じ扱い） | 表現の一致 | 自動 |
| T-36 | タグを持つ `ChunkPayload` | `BuildPayload` → `ExtractTags` | 書いた表現をそのまま復元できる（**書き込みと復元の表現の一致**を往復で固定する。本番の欠陥は復元側だけで、書き込み側に呼び出し元は無い） | 表現の一致（検索結果一覧・#642） | 自動 |
| T-37 | 埋め込みが空ベクトル（ゲートウェイの縮退）、hybrid | `SearchAsync` | **ベクトル系統を呼ばない**（呼ぶと 0 次元クエリで実機が失敗する）。全文の結果は残る | 縮退（設計上） | 自動 |
| T-38 | 同上、`Mode = semantic` | 同上 | **0 件**。全文へ振り替えない（利用者が選んだモードを変えない） | 縮退（設計上） | 自動 |
| T-39 | 同上、`POST /search` | エンドポイント結合 | **200 ＋ `SearchResponse` の形**（`results` が配列・`totalHits` が数値）。実機の統合スタックが見ているのと同じ条件 | 縮退（設計上） | 自動 |
| T-40 | 埋め込みゲートウェイへ到達できない（`HttpRequestException`） | `SearchAsync` | 🔴 **例外が伝播する**（200 ＋ 空へ潰さない。潰すと後段が死んでも緑になる） | 故障を隠さない | 自動 |

## テストデータ

- `ChunkPayload`: 1536 次元ゼロベクトル＋日本語本文＋ `s3://bucket/{guid}.md` 形式の `MarkdownUri`＋任意の ABAC 属性（`dept`, `confidentiality`）。
- `SearchResultDto`: `ReciprocalRankFusion` 用に `ChunkId` を変えたヒット群（`HybridSearchServiceTests.Hit`。**`updatedAt` は任意**で、未再索引の縮退を再現できる）。
- `Qdrant.Client.Grpc.Value` ペイロード辞書（フラットキー／ネスト構造体の両表現）。
- **タグ**: `tags` を `ListValue` で持つペイロード辞書と、`Tags` を持つ `ChunkPayload`（`["経理","規程"]`）。

## 関連仕様

- 機能仕様書: `../functional/FR-03_hybrid-search.md`
- 作業仕様書: `../../.ai-context/specs/20260627_FR-03_hybrid-search.md` ／ `../../.ai-context/specs/20260809_issue-642_qdrant-tag-restoration.md`
- 画面仕様書: `../screens/SC-02_search-results.md`（タグ列の表示元）
- 通信仕様書: `../api/openapi.yaml`（`/search`）
- データ仕様書: `../data/document-and-version.md`（未整備の場合あり）

## 未決事項

- 実 Qdrant を用いた全文 Match・full-text index 未作成時の graceful degradation は統合テスト（属性ペイロードの実機検証で確認予定）で別途検証する。
- E2E（実埋め込み・実ベクトルDB）での精度/p95 検証は負荷試験タスク（**#196**）で別途実施。ハーネスは
  `perf/k6/`（`search-load.js` p95<1500）、テスト仕様は `NFR-01_performance-load-test.md`。実測は環境準備後。
</content>

<!-- trace-table:
row1: SC-02
row2: SC-02
row3: SC-02
-->
