---
title: 作業仕様書 — コンポーザビリティ対応: 既存実装の固定部分と可変部分の分離（フォルダ構成再編）
type: spec
status: in-progress
related_ids:
  - FR-14
  - FR-15
  - ADR-0018
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
  - "../../planning/projects/microservices-platform/06_technical/10_composability-design.md"
related_specs:
  - ../tech/composability-classification.md
  - ../tech/tech-requirements.md
  - ../adr/IADR-0027_composability-folder-structure.md
---

# 作業仕様書: 既存実装の固定部分と可変部分の分離（フォルダ構成再編）

Issue: #102（`[FR-14, FR-15 | ADR-0018] コンポーザビリティ対応: 既存実装の固定部分と可変部分の分離`）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-14（構成の組み替え容易性）・FR-15（構成情報取得API — 本作業では分離の前提整備のみ）
- ユースケース（UC）: —（運用・保守要求）
- 画面（SC）: —（SC-11 構成ビューアは後続）
- 関連 ADR: ADR-0018（Accepted）・ADR-0002（DB per Service）・ADR-0003（MassTransit+RabbitMQ）
- 計画書リンク: `planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md`、`06_technical/10_composability-design.md`

## 目的・背景

ADR-0018 は、システムを**固定（土台）**（同期 API 経路・ABAC・メッセージ基盤・イベントエンベロープ標準・正規化形式）と
**可変（組み替え可能）**（パイプライン段・イベントバインディング・ポート実装の選択・コネクタ）に区分した。
本作業は issue #102 の最初の一歩として、FR-01〜13 実装済みの既存コードをこの区分に沿って**構造的に分離**する。
具体的には、各 VS プロジェクトのフォルダ階層を「固定＝`Foundation/`」「可変＝`Composable/`」の二分構造へ再編し、
どのコードが土台でどのコードが組み替え対象かを、フォルダを見るだけで判別できるようにする。

あわせて、今後サービスを Git サブモジュールとして追加配置しやすいよう、サービスユニットの標準レイアウトを規約化する。

## 対象範囲

- 対象:
  1. **棚卸し**: 同期呼び出し関係・イベント発行/購読関係・外部コンポーネント依存の洗い出しと、固定/可変の区分表（実装版）の作成（`docs/tech/composability-classification.md`）
  2. **フォルダ再編**: 全サービス（11）＋ BFF ＋ `KnowledgePlatform.Shared.Infrastructure` の `Foundation/` / `Composable/` 二分構造への移動と、名前空間のフォルダ一致
  3. **サービスユニット規約**: `src/Services/README.md`（標準レイアウト・サブモジュール配置規約）
  4. **記録**: 分離方針の IADR 起票（IADR-0027）
- 対象外（issue #102 の残項目・後続 PR / 後続 issue）:
  - イベント共通エンベロープの導入（現行イベント 6 種の契約変更を伴うため別 PR とする）
  - 共通ステップインタフェース（`Subscribe/Process/Publish`）の導入（エンベロープと同時に設計する）
  - 同期 API 契約の IDL 明文化は**既存** `docs/api/openapi.yaml`（CI 自動更新）を正とし、本作業では区分表から参照するのみ
  - 宣言的パイプライン構成・構成情報 API・ドリフト検出（issue #102 記載の後続 issue）

## 設計

### 1. 区分の適用規則（ADR-0018 → コード構造への写像）

| ADR-0018 の区分 | コード上の対応 | 配置 |
| --- | --- | --- |
| 固定: 同期 API 経路 | Minimal API エンドポイント（BFF→各サービス、AI分析→認可→検索） | `Foundation/Endpoints/` |
| 固定: ドメイン・正規化形式・ABAC | エンティティ・ドメインサービス（正規化・ABAC 評価・ハイブリッド検索・RAG 編成・エグレス統制） | `Foundation/Domain/`・`Foundation/Services/` |
| 固定: 永続化（DB per Service） | DbContext | `Foundation/Persistence/`（EF `Migrations/` はツール既定のため直下維持） |
| 固定: 差し替え点の契約 | ポート抽象（インタフェース・オプション型） | `Foundation/Ports/` |
| 可変: パイプライン段 | MassTransit コンシューマ（イベント購読→処理→発行） | `Composable/Steps/` |
| 可変: ポート実装の選択 | ポート実装（Qdrant / MinIO(S3) / Wiki.js / LlmGateway クライアント / Pandoc / Claude / Voyage 等） | `Composable/Adapters/` |
| 可変: コネクタ | データソースコネクタ（未実装。将来 DataSourceService の `Composable/` 配下へ） | `Composable/Connectors/`（予約） |

- 名前空間はフォルダ階層に一致させる（例: `ConversionService.Worker.Composable.Steps`）。参照側の `using` で
  固定/可変の依存が可視化されることを狙う。
- `Program.cs` は合成ルート（可変部分を構成で束ねる場所）としてプロジェクト直下に置く。
- `Migrations/` は EF Core ツールの既定出力先（既存移行と同じフォルダへ生成）を崩さないためプロジェクト直下に残す。
- テスト支援用の `TestMarker` はプロジェクト直下（ルート名前空間）へ置く。
- `KnowledgePlatform.Shared.Contracts` は全体が「固定＝契約」そのものであるため再編しない（区分表に明記）。

### 2. 依存方向の規則

- `Foundation/` は `Composable/` に依存してはならない（ポート抽象を介する）。
- `Composable/Steps/` は `Shared.Contracts` のイベント型と自プロジェクトの `Foundation/Ports/` のみに依存する。
- 段間の連携はイベント経由のみとし、段どうしの直接参照（型共有・同期呼び出し）を持たない。

### 3. サービスユニット標準レイアウト（サブモジュール配置考慮）

```
src/Services/<ServiceName>/        ← サービスユニット（将来のサブモジュール境界）
  src/<ServiceName>.<Api|Worker>/
    Program.cs  appsettings*.json  TestMarker.cs  Migrations/
    Foundation/{Endpoints,Domain,Persistence,Ports,Services}/
    Composable/{Steps,Adapters,Connectors}/
  tests/<ServiceName>.<Api|Worker>.Tests/
```

- サービスユニットは自ユニット外へは `src/Shared/`（相対パス `../../../../Shared/`）のみ参照可とする。
  サービス間のコード参照を禁止することで、任意のユニットをサブモジュール（別リポジトリ）へ切り出せる。
- ビルド共通設定（`src/Directory.Build.props`・`src/Directory.Packages.props`）はディレクトリ階層で継承されるため、
  サブモジュールを `src/Services/<Name>/` へ配置すれば追加設定なしで適用される。
- 詳細は `src/Services/README.md` に規約として明文化する。

### 4. 主な移動対象（プロジェクト別サマリ）

| プロジェクト | Composable/Steps | Composable/Adapters | 備考 |
| --- | --- | --- | --- |
| ConversionService.Worker | RawDocumentFetchedConsumer | Pandoc・LlmGatewayDiagramCoder・StorageObjectStore | 正規化（NormalizationService）は固定＝Foundation/Services |
| IngestionService.Worker | DocumentUpdatedConsumer | LlmGatewayEmbedding・Qdrant・StorageReader・MarkdownChunking | ChunkId は固定規約＝Foundation/Domain |
| DocumentService.Api | DocumentNormalizedConsumer | — | |
| WikiService.Api | DocumentSync/DocumentDeletedConsumer | WikiJsGraphQlClient・StorageMarkdownReader | ABAC 関連は Foundation/Services |
| LlmGateway.Api | — | Claude/Copilot/SelfHosted/Voyage 各プロバイダ | ルーティング・エグレス統制は固定＝Foundation/Routing |
| RetrievalService.Api | — | Qdrant/InMemory VectorStore・LlmGatewayEmbedding | HybridSearch は固定＝Foundation/Services |
| DataSourceService.Api / AiAnalysis / Authorization / Feedback / Dashboard / Bff | — | — | 全て Foundation（同期経路・ABAC・ドメイン） |
| Shared.Infrastructure | — | S3/Null ObjectStorageClient・Bootstrap・実装選択ヘルパ（ObjectStorageExtensions） | 基盤 Extensions/Middleware と Ports は Foundation |

## 受け入れ基準

issue #102 の受け入れ基準のうち、本作業で満たすもの:

- [x] 固定/可変の区分表（実装版）が作成され、全サービスの依存が分類されている（`docs/tech/composability-classification.md`）
- [x] 同期 API 契約が IDL としてリポジトリ管理されている（既存 `docs/api/openapi.yaml` を区分表から参照・確認）
- [x] ポートを迂回した外部コンポーネント直接依存が存在しない（棚卸しで確認。逸脱は検出されず）
- [x] 既存の回帰テストが全て成功する（13 テストプロジェクト・317 件、失敗 0。2026-07-08 実施）
- [x] 分離方針が IADR として記録されている（IADR-0027）

本作業に固有の完了条件:

- [x] 全対象プロジェクトが `Foundation/` / `Composable/` 二分構造に再編され、名前空間がフォルダに一致している
- [x] `Foundation/` → `Composable/` 方向のコード参照が存在しない（`ObjectStorageExtensions` は合成コードとして Composable へ移動して解消）
- [x] `src/Services/README.md` にサービスユニット規約（サブモジュール配置含む）が記載されている

後続へ送るもの（本 PR では満たさない）:

- 共通ステップインタフェース準拠・イベント共通エンベロープ（issue #102 内の残項目として報告）

## テスト方針

- 本作業は**振る舞いを変えない構造リファクタリング**であり、新規テストは追加しない。
- 既存の全テスト（単体・MassTransit TestHarness・統合）を回帰として実行し、全件成功を完了条件とする。
- 名前空間変更の取りこぼしはビルドエラーで検出されるため、プロジェクト単位でビルドを回しながら移行する。

## 計画書との差異

- 差異: なし（ADR-0018 の区分をそのまま適用。エンベロープ・ステップ IF の詳細設計は計画どおり実装リポジトリの後続作業とする）

## 未決事項

- `Composable/Steps/` の共通ステップインタフェース導入時に、名前空間・フォルダの再調整が生じ得る（後続 PR で判断）。
