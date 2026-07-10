---
title: ハイブリッド検索 テスト仕様書
type: test-spec
status: in-progress
related_ids:
  - FR-03
  - UC-01
author: claude
created: 2026-07-04
updated: 2026-07-04
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
---

# テスト仕様書: ハイブリッド検索

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-03
- ユースケース（UC）: UC-01
- 受け入れ基準の所在（02_requirements）: `02_requirements/01_requirements.md`
- 計画書リンク: 同上 / `07_adr/ADR-0009`（Qdrant ベクトルDB）

## テスト対象・範囲

- 対象: RRF 融合ロジック（`HybridSearchService.ReciprocalRankFusion`）、`/search` エンドポイント結合（`InMemoryVectorStore`）、ABAC フィルタ適用（両系統）、Qdrant ペイロードからの ABAC 属性復元（`QdrantVectorStore.ExtractAttributes`）。
- 対象外: 実 Qdrant の full-text Match 挙動・実埋め込みモデルの精度、反映時間（インジェスト責務）、負荷/p95、画面。

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

## テストデータ

- `ChunkPayload`: 1536 次元ゼロベクトル＋日本語本文＋ `s3://bucket/{guid}.md` 形式の `MarkdownUri`＋任意の ABAC 属性（`dept`, `confidentiality`）。
- `SearchResultDto`: `ReciprocalRankFusion` 用に `ChunkId` を変えたヒット群（`HybridSearchServiceTests.Hit`）。
- `Qdrant.Client.Grpc.Value` ペイロード辞書（フラットキー／ネスト構造体の両表現）。

## 関連仕様

- 機能仕様書: `../functional/FR-03_hybrid-search.md`
- 作業仕様書: `../specs/20260627_FR-03_hybrid-search.md`
- 通信仕様書: `../api/openapi.yaml`（`/search`）
- データ仕様書: `../data/document-and-version.md`（未整備の場合あり）

## 未決事項

- 実 Qdrant を用いた全文 Match・full-text index 未作成時の graceful degradation は統合テスト（IADR-0014 で確認予定）で別途検証する。
- E2E（実埋め込み・実ベクトルDB）での精度/p95 検証は負荷試験タスク（**#196**）で別途実施。ハーネスは
  `perf/k6/`（`search-load.js` p95<1500）、テスト仕様は `NFR-01_performance-load-test.md`。実測は環境準備後。
</content>
