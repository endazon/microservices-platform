---
title: 固定/可変 区分表（実装版）— コンポーザビリティ対応の棚卸し
type: tech
status: completed
created: 2026-07-08
updated: 2026-08-21
author: claude
---
<!-- trace:
ids: [FR-01, FR-02, FR-03, FR-04, FR-05, FR-06, FR-07, FR-08, FR-09, FR-10, FR-11, FR-12, FR-13, FR-14, FR-15]
adrs: [ADR-0015, ADR-0018]
iadrs: [IADR-0002, IADR-0007, IADR-0014, IADR-0021, IADR-0022, IADR-0023, IADR-0024, IADR-0025, IADR-0027, IADR-0051, IADR-0053, IADR-0054, IADR-0055, IADR-0059]
specs: [20260708_issue-102_composability-fixed-variable-separation]
issues: [#102, #195, #217, #218, #219, #229]
-->

# 固定/可変 区分表（実装版）

Issue #102の作業項目 1「棚卸し」の成果物である。
実装済みの機能群（データソース登録から Wiki 閲覧まで）のコード依存を洗い出し、コンポーザブルアーキテクチャの決定が定める「固定（土台）/ 可変（組み替え可能）」区分へ分類する。
コード上の配置規約（`Foundation/` / `Composable/`）は、固定/可変分離のフォルダ・名前空間規約を参照。

## 1. 同期呼び出し関係（すべて固定）

同期 API 経路はコンポーザブルアーキテクチャの決定で**構成による組み替えの対象外**（変更は新 ADR）。契約は
[docs/api/openapi.yaml](../api/openapi.yaml)（CI `openapi.yml` で自動更新）でバージョン管理する。

| 呼び出し元 | 呼び出し先 | 用途 | 契約 |
| --- | --- | --- | --- |
| Bff | AiAnalysisService / FeedbackService / DashboardService | 画面向け集約 | openapi.yaml |
| AiAnalysisService | AuthorizationService → RetrievalService → LlmGateway | RAG（認可→検索→生成） | openapi.yaml |
| WikiService | AuthorizationService | 閲覧時 ABAC 判定 | openapi.yaml |
| RetrievalService / IngestionService | LlmGateway（/embeddings） | 埋め込み生成（ポート経由） | openapi.yaml |
| ConversionService | LlmGateway（/completions） | 図の PlantUML/Mermaid 化（ポート経由） | openapi.yaml |

> LlmGateway への埋め込み・補完呼び出しは同期 HTTP だが、呼び出し側はポート
> （`IEmbeddingService` / `IDiagramCoder`）を介しており、実装差し替えは可変（§3）。
> **経路の存在自体**（どのサービスがどの API に依存してよいか）が固定である。

## 2. イベント発行・購読関係（バインディングは可変）

イベント契約型は `Knowledge.Contracts/Events/`（knowledge ユニット固有契約。固定＝契約。スキーマは後方互換の追加のみ許可。#229・契約の階層化）。
発行・購読の**バインディング**は可変であり、後続 issue で宣言的構成から生成する対象。

| イベント | 発行者 | 購読者（段） | 備考 |
| --- | --- | --- | --- |
| RawDocumentFetched | DataSourceService（同期 API 内） | ConversionService.RawDocumentFetchedConsumer | パイプライン起点 |
| DocumentNormalized | ConversionService | DocumentService.DocumentNormalizedConsumer | 正規化完了 |
| DocumentUpdated | DocumentService | IngestionService.DocumentUpdatedConsumer / WikiService.DocumentSyncConsumer | ファンアウト |
| DocumentDeleted | DocumentService | WikiService.DocumentDeletedConsumer | 削除伝播 |
| IngestionCompleted | IngestionService | （現在購読者なし） | 完了通知 |
| IngestionRequested | （現在発行者なし） | — | 契約のみ定義済み |

パイプライン（変換・取り込み）:
`DataSource → [RawDocumentFetched] → Conversion → [DocumentNormalized] → Document → [DocumentUpdated] → {Ingestion, Wiki}`

- 段間の直接依存（イベントを介さない呼び出し・型共有）: **なし**（棚卸しで確認。段が共有するのは Shared.Contracts のイベント型のみ）
- イベント共通エンベロープ（文書ID・バージョン・ソースメタ・ABAC属性ヒント・トレースID）: **未適用**。
  現行 6 イベントは個別レコード型。エンベロープ標準化は後続 PR（issue #102 残項目）。

## 3. ポートと実装（抽象は固定、実装の選択は可変）

| ポート（固定＝契約） | 定義場所 | 実装（可変） | 外部コンポーネント | 選択手段 |
| --- | --- | --- | --- | --- |
| `ILlmProvider` / `IEmbeddingProvider` | LlmGateway | Claude / Copilot / SelfHosted / Voyage / SelfHostedEmbedding | Anthropic API・Copilot・セルフホスト・Voyage AI | 構成（ルーティング表。設定駆動のエンドポイント定義・既定モデルの追加・埋め込みの機密区分ルーティング） |
| `IEmbeddingService` | Retrieval / Ingestion | LlmGatewayEmbeddingService | LlmGateway 経由 | DI 登録 |
| `IVectorStore` / `IIngestionVectorStore` | Retrieval / Ingestion | QdrantVectorStore / InMemoryVectorStore / QdrantIngestionVectorStore | Qdrant | 構成（接続文字列の有無。取り込みパイプラインと Qdrant ペイロード表現の実装判断） |
| `IObjectStorageClient` | Shared.Infrastructure | S3ObjectStorageClient / NullObjectStorageClient | MinIO（S3 互換。バケット/キー設計は実装 ADR が定める） | 構成（エンドポイント有無） |
| `IObjectStore` / `IDocumentContentReader` / `IWikiContentReader` | Conversion / Ingestion / Wiki | Storage* 系（IObjectStorageClient へ委譲） | MinIO | DI 登録 |
| `IBodyConverter` | Conversion | PandocConversionService | pandoc（プロセス） | DI 登録 |
| `IDiagramCoder` | Conversion | LlmGatewayDiagramCoder | LlmGateway 経由 | DI 登録 |
| `IWikiJsClient` | Wiki | WikiJsGraphQlClient | Wiki.js（GraphQL push 同期） | DI 登録 |
| `IChunkingService` | Ingestion | MarkdownChunkingService | —（内部戦略） | DI 登録 |
| `IDataSourceConnector` | DataSource | FileSystemConnector / WikiConnector / SaaSConnector / DatabaseConnector（`ConnectorRegistry` が SourceType で解決） | ファイルサーバー / Wiki / SaaS / 業務DB | DI 登録（#195/#217/#218/#219。コネクタのポート分離と各コネクタの実装判断による。未登録型は縮退） |

- ポートを迂回した外部コンポーネント直接依存: **なし**（Qdrant SDK・S3 SDK・Wiki.js GraphQL・Anthropic SDK・pandoc の
  使用箇所は全て上記ポート実装内に閉じている。棚卸しで確認）。

## 4. サービス別区分（フォルダ配置）

各プロジェクト内の配置は `Foundation/`（固定）/ `Composable/`（可変）。詳細規約は固定/可変分離のフォルダ・名前空間規約による。

| サービス | 固定（Foundation） | 可変（Composable） |
| --- | --- | --- |
| Bff | 集約エンドポイント（同期経路） | — |
| AuthorizationService | ABAC 評価・属性/ポリシー管理・DB | — |
| DocumentService | 文書カタログ・版管理・API・DB | DocumentNormalizedConsumer（段） |
| DataSourceService | データソース登録 API・DB | コネクタ（filesystem/wiki/saas/db）・同期オーケストレータ |
| ConversionService | 正規化サービス（正規化形式＝固定）・冪等 ID・ポート | 変換段・Pandoc/LLM図/ストレージ各アダプタ |
| IngestionService | チャンク ID 規約・ポート | 取り込み段・埋め込み/Qdrant/ストレージ/チャンク各アダプタ |
| RetrievalService | ハイブリッド検索・検索 API・ポート | Qdrant/InMemory/埋め込み各アダプタ |
| AiAnalysisService | RAG 編成（認可→検索→生成の固定経路）・API | — |
| LlmGateway | ルーティング・エグレス統制（越境統制＝固定）・API・ポート | LLM/埋め込み各プロバイダ |
| WikiService | ABAC ページフィルタ・閲覧 API・DB・ポート | 同期段（2）・Wiki.js/ストレージ各アダプタ |
| FeedbackService / DashboardService | API・DB | — |
| Shared.Contracts | 全体（同期 DTO・イベント契約） | — |
| Shared.Infrastructure | 認証・可観測性・メッセージ基盤・相関 ID・ストレージポート | S3/Null ストレージアダプタ・実装選択ヘルパ（ObjectStorageExtensions） |

## 5. メッセージ基盤・横断基盤（固定）

| 要素 | 場所 | 区分 |
| --- | --- | --- |
| MassTransit + RabbitMQ 配線（`AddPlatformMassTransit`） | Shared.Infrastructure | 固定（基盤そのもの。トポロジの宣言化は後続） |
| JWT/Keycloak 認証・ロール変換 | Shared.Infrastructure | 固定 |
| OTel 可観測性・相関 ID | Shared.Infrastructure | 固定 |
| ヘルスチェック | Shared.Infrastructure | 固定 |

## 6. 発見した逸脱と対処

| 逸脱 | 区分 | 対処 |
| --- | --- | --- |
| イベントが共通エンベロープ未適用 | 計画とのギャップ（コンポーザブルアーキテクチャの決定 §3） | 後続 PR で標準化（issue #102 残項目として報告） |
| 段が共通ステップインタフェース未準拠 | 同上 | エンベロープと同時に導入 |
| `IngestionRequested` / `IngestionCompleted` が未接続（発行者/購読者なし） | 情報 | 宣言的構成導入時にバインディング定義で扱う |
| ポート迂回の直接依存 | — | 検出されず（対処不要） |
