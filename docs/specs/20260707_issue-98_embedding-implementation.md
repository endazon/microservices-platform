---
title: 作業仕様書 — FR-02/UC-04 埋め込み生成の実体実装（Voyage 既定＋高機密セルフホスト）
type: spec
status: done
related_ids:
  - FR-02
  - FR-03
  - FR-05
  - UC-04
author: claude
created: 2026-07-07
updated: 2026-07-07
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-02, FR-03, FR-05)"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md (UC-04)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0016_embedding-provider-voyage.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0017_selfhosted-embedding-ruri.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0013_embedding-model.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0009_vector-store-qdrant.md"
related_specs:
  - ../adr/IADR-0025_embedding-provider-routing-and-model-collections.md
  - ../adr/IADR-0007_llm-egress-routing-config-driven.md
  - ../adr/IADR-0022_default-opus-and-fable5-copilot-routes.md
  - ../adr/IADR-0014_qdrant-attribute-payload-key.md
related_adrs:
  - ADR-0016 (埋め込みは voyage-3.5 既定＋高機密セルフホスト併用)
  - ADR-0017 (セルフホスト埋め込みは Ruri v3 を第一採用)
  - ADR-0013 (埋め込みは Embed ポートで抽象化)
  - ADR-0009 (ベクトルストアは Qdrant)
  - IADR-0025 (本作業の実装 ADR)
---

# 作業仕様書: FR-02/UC-04 埋め込み生成の実体実装

## 目的（Issue #98）

計画 [ADR-0016](../../planning/projects/microservices-platform/07_adr/ADR-0016_embedding-provider-voyage.md)
（Accepted、埋め込みは voyage-3.5 既定＋高機密セルフホスト併用）および
[ADR-0017](../../planning/projects/microservices-platform/07_adr/ADR-0017_selfhosted-embedding-ruri.md)
（Accepted、セルフホストは Ruri v3 第一採用）の決定に従い、空配列スタブだった `/embed` の
埋め込み生成実体を結線する。

## 背景・現状（調査結果）

- 全プロバイダの `ILlmProvider.EmbedAsync` が `Array.Empty<float>()` を返すスタブで、埋め込み生成の
  実体が未結線（計画リポ精査 `draft/feedback/20260706_tech-stack-implementation-status.md` 乖離3）。
- `/embed` は既定プロバイダ（Claude）の `EmbedAsync` を呼ぶだけで、機密区分による越境判定が無い。
- Qdrant コレクションの次元は暫定 1536。ADR-0016 で voyage-3.5 の 1024 へ変更し、機密区分でモデルが
  分かれるためコレクションをモデル別に分離する必要がある。
- 埋め込みは取り込み時に**全文書本文**を送信するため、LLM 呼び出しよりデータ露出が大きい。

## 実装方針

### 1. Shared 契約（`KnowledgePlatform.Shared.Contracts`）
- `EmbedApiRequest(Text, Confidentiality?, Purpose?)` / `EmbedApiResponse(Vector, Dimensions, Model,
  Collection, Embedded, Endpoint?, RoutingReason?)` を新設し、Gateway と呼び出し側（Ingestion/Retrieval）で
  契約を一元管理する（`/complete` の `CompletionApiRequest/Response` と同じ方針）。

### 2. LlmGateway: 埋め込みプロバイダ（`IEmbeddingProvider`）
- `VoyageEmbeddingProvider`（キー `voyage`, ティアB）: Voyage AI `/v1/embeddings` への REST アダプタ。
  `voyage-3.5`・1024 次元。API キーは `Embedding:Voyage:ApiKey`（Secret 経由・既定空）。
- `SelfHostedEmbeddingProvider`（キー `selfhosted-embedding`, ティアA）: OpenAI 互換 `/v1/embeddings` の
  セルフホスト基盤（Ruri v3・768 次元）。`Embedding:SelfHosted:BaseUrl` 未設定時は利用不可（fail-closed）。

### 3. LlmGateway: 埋め込みルーティング（機密区分ティア判定・fail-closed）
- `EmbeddingEgress`: 埋め込み専用の越境ポリシー。一般 LLM 越境（`EgressMatrix`）より**厳格**にし、
  `confidential` / `restricted` はティアA（セルフホスト）固定とする（LLM 越境では confidential に
  ティアB も許容していたが、埋め込みは本文全量送信のためティアB を許容しない）。未指定・未知は安全側
  （restricted 相当＝ティアA）。
- `EmbeddingRouter` / `IEmbeddingRouter`: 機密区分と用途（index/query）から、送信先エンドポイント・
  モデル・次元・コレクションを決定、または送信を拒否（fail-closed）する。設定駆動（`EmbeddingRoutingOptions`,
  `Embedding:Routing`）。クエリ埋め込み（`Purpose=query`）は検索対象コレクション（voyage/1024）へ整合させる
  ため既定外部経路（ティアB）へ固定する。
- `/embed` を上記ルーターで結線。fail-closed 時は `Embedded=false`・空ベクトルを返し外部送信しない。
  次元不整合時も `Embedded=false`（誤次元ベクトルを索引しない）。
- 旧 `ILlmProvider.EmbedAsync`（空配列スタブ）を削除し、埋め込みを `IEmbeddingProvider` へ分離する。

### 4. IngestionService
- `IEmbeddingService` を機密区分対応にし（`EmbeddingResult(Vector, Collection, Embedded)` を返す）、
  `DocumentUpdatedConsumer` が文書属性 `confidentiality` を渡す。
- `IIngestionVectorStore` をモデル別コレクション対応にする。取り込み冒頭で**全モデル別コレクション**から
  当該文書を削除し、機密区分変更（例 public→confidential）時の旧コレクション残存（ABAC バイパス）を防ぐ。
- fail-closed（`Embedded=false`）のチャンクは索引しない。
- 起動時ブートストラップで全モデル別コレクションを実次元で作成する。

### 5. RetrievalService
- クエリ埋め込みを既定経路（voyage・1024 次元）へ整合させる（`Purpose=query`）。
- 検索対象コレクションを voyage（`knowledge_chunks_voyage_3_5`）へ変更する。高機密（ruri/768）コレクションの
  横断検索は FR-03 の後続課題（本作業の対象外）。

### 6. 設定・配備
- Voyage API キー / セルフホスト URL はシークレット経由（既定空）。`deploy/docker-compose.yml` の
  llm-gateway に環境変数（既定空）を追加する。

## 受け入れ基準（Issue #98）

- [x] 取り込みで実ベクトル（1024 次元）がモデル別 Qdrant コレクションへ索引される（空配列スタブの解消）。
- [x] `confidential` / `restricted` 文書の本文が外部埋め込み API へ送信されない（fail-closed をテストで担保）。
- [x] Voyage のゼロ保持（オプトアウト）設定が運用ドキュメントに記録される（`docs/operations/operations.md`）。
- [x] モデル別コレクション分離と再索引手順が `docs/` に記録される（IADR-0025 起票・運用仕様書）。

## テスト方針

- LlmGateway: 機密区分ルーティング（public→voyage/1024、confidential→ティアA固定・セルフホスト無効なら
  fail-closed で外部未送信）、次元整合、クエリ経路。`EmbeddingRouter` 単体テスト。
- Ingestion: fail-closed 索引スキップ、全コレクション残存削除、confidentiality 伝播、次元整合。

## トレーサビリティ

- ブランチ `feat/FR-02-issue-98-embedding-implementation` / コミット `feat(FR-02,UC-04): ...`。
- 実装 ADR: [IADR-0025](../adr/IADR-0025_embedding-provider-routing-and-model-collections.md)。
