---
title: 固定/可変 区分表（実装版）— コンポーザビリティ対応の棚卸し
type: tech
status: fixed
related_ids:
  - FR-14
  - FR-15
  - ADR-0018
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
  - "../../planning/projects/microservices-platform/06_technical/10_composability-design.md"
related_specs:
  - ../specs/20260708_issue-102_composability-fixed-variable-separation.md
  - ../adr/IADR-0027_composability-folder-structure.md
  - ../api/openapi.yaml
---

# 固定/可変 区分表（実装版）

Issue #102（FR-14/FR-15, ADR-0018）の作業項目 1「棚卸し」の成果物である。
FR-01〜13 実装済みコードの依存を洗い出し、ADR-0018 の「固定（土台）/ 可変（組み替え可能）」区分へ分類する。
コード上の配置規約（`Foundation/` / `Composable/`）は [IADR-0027](../adr/IADR-0027_composability-folder-structure.md) を参照。

## 1. 同期呼び出し関係（すべて固定）

同期 API 経路は ADR-0018 で**構成による組み替えの対象外**（変更は新 ADR）。契約は
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

イベント契約型は `KnowledgePlatform.Shared.Contracts/Events/`（固定＝契約。後方互換の追加のみ許可）。
発行・購読の**バインディング**は可変であり、後続 issue で宣言的構成から生成する対象。

| イベント | 発行者 | 購読者（段） | 備考 |
| --- | --- | --- | --- |
| RawDocumentFetched | DataSourceService（同期 API 内） | ConversionService.RawDocumentFetchedConsumer | パイプライン起点 |
| DocumentNormalized | ConversionService | DocumentService.DocumentNormalizedConsumer | 正規化完了 |
| DocumentUpdated | DocumentService | IngestionService.DocumentUpdatedConsumer / WikiService.DocumentSyncConsumer | ファンアウト |
| DocumentDeleted | DocumentService | WikiService.DocumentDeletedConsumer | 削除伝播（IADR-0023） |
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
| `ILlmProvider` / `IEmbeddingProvider` | LlmGateway | Claude / Copilot / SelfHosted / Voyage / SelfHostedEmbedding | Anthropic API・Copilot・セルフホスト・Voyage AI | 構成（ルーティング表, IADR-0007/0022/0025） |
| `IEmbeddingService` | Retrieval / Ingestion | LlmGatewayEmbeddingService | LlmGateway 経由 | DI 登録 |
| `IVectorStore` / `IIngestionVectorStore` | Retrieval / Ingestion | QdrantVectorStore / InMemoryVectorStore / QdrantIngestionVectorStore | Qdrant | 構成（接続文字列有無, IADR-0002/0014） |
| `IObjectStorageClient` | Shared.Infrastructure | S3ObjectStorageClient / NullObjectStorageClient | MinIO（S3 互換, ADR-0015/IADR-0024） | 構成（エンドポイント有無） |
| `IObjectStore` / `IDocumentContentReader` / `IWikiContentReader` | Conversion / Ingestion / Wiki | Storage* 系（IObjectStorageClient へ委譲） | MinIO | DI 登録 |
| `IBodyConverter` | Conversion | PandocConversionService | pandoc（プロセス） | DI 登録 |
| `IDiagramCoder` | Conversion | LlmGatewayDiagramCoder | LlmGateway 経由 | DI 登録 |
| `IWikiJsClient` | Wiki | WikiJsGraphQlClient | Wiki.js（GraphQL, IADR-0021） | DI 登録 |
| `IChunkingService` | Ingestion | MarkdownChunkingService | —（内部戦略） | DI 登録 |
| データソースコネクタ | — | **未実装**（DataSourceService は登録メタのみ） | ファイルサーバー等 | 後続（09_datasource-connectors） |

- ポートを迂回した外部コンポーネント直接依存: **なし**（Qdrant SDK・S3 SDK・Wiki.js GraphQL・Anthropic SDK・pandoc の
  使用箇所は全て上記ポート実装内に閉じている。棚卸しで確認）。

## 4. サービス別区分（フォルダ配置）

各プロジェクト内の配置は `Foundation/`（固定）/ `Composable/`（可変）。詳細規約は IADR-0027。

| サービス | 固定（Foundation） | 可変（Composable） |
| --- | --- | --- |
| Bff | 集約エンドポイント（同期経路） | — |
| AuthorizationService | ABAC 評価・属性/ポリシー管理・DB | — |
| DocumentService | 文書カタログ・版管理・API・DB | DocumentNormalizedConsumer（段） |
| DataSourceService | データソース登録 API・DB | （将来: コネクタ） |
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
| イベントが共通エンベロープ未適用 | 計画とのギャップ（ADR-0018 §3） | 後続 PR で標準化（issue #102 残項目として報告） |
| 段が共通ステップインタフェース未準拠 | 同上 | エンベロープと同時に導入 |
| `IngestionRequested` / `IngestionCompleted` が未接続（発行者/購読者なし） | 情報 | 宣言的構成導入時にバインディング定義で扱う |
| ポート迂回の直接依存 | — | 検出されず（対処不要） |
