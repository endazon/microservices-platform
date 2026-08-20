---
title: 作業仕様書 — FR-11 用途別・機密度別 LLM ルーティングの実運用不具合修正
type: spec
status: completed
related_ids:
  - FR-11
  - FR-02
  - UC-02
author: claude
created: 2026-07-04
updated: 2026-07-04
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-11, FR-02)
  - planning:projects/microservices-platform/03_usecases/01_usecases.md (UC-02)
  - planning:projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
related_specs:
  - ./20260702_FR-11_llm-egress-routing.md
  - ./20260703_FR-12_document-normalization-pipeline.md
  - ./20260627_FR-05_abac-deny-by-default.md
related_adrs:
  - IADR-0007 (config 駆動 LLM ルーティング)
  - IADR-0004 (ABAC 多値 allow-list / deny-by-default)
  - IADR-0014 (Qdrant 属性ペイロードのキー表現)
  - ADR-0010 (外部マネージドAPI主体のLLMゲートウェイ)
---

# 作業仕様書: FR-11 用途別・機密度別 LLM ルーティングの実運用不具合修正

親 Issue: #58（#48 横断監査 `adr-guardian` 検出）。IADR-0007（config 駆動 LLM ルーティング）の意図が
実運用経路で満たされない不具合 2 件＋要確認 1 件を修正する。

## 背景（監査で検出した事象）

1. **purpose キー不一致（用途別モデル選択が効かない）**
   - ConversionService は `Purpose: "diagram-coding"` を `/complete` へ送る
     （`LlmGatewayDiagramCoder.cs`）。
   - LlmGateway の `Llm:Routing:PurposeModels` の設定キーは `"diagram"`（`appsettings.json`）。
   - `LlmRouter.ResolveModel` は `PurposeModels.TryGetValue(request.Purpose, …)` で照合するため、
     `"diagram-coding"` は一致せず、図コード化が用途別モデル（haiku）でなく既定モデルで呼ばれていた。

2. **Qdrant ペイロードの属性未復元（機密区分判定が常に restricted へ縮退）**
   - `QdrantVectorStore.MapPayload` が常に `Attributes: []` を返す。
   - AiAnalysisService の `RagOrchestrator.HighestConfidentiality` は「属性欠落 → restricted」へ
     倒れるため、本番 Qdrant 経路では常に restricted となり、FR-11 の機密区分別ルーティングが
     事実上無効化されていた（漏えい方向でなく安全側だが、用途どおりに機能しない）。

3. **（要確認）Qdrant フィルタキーのドット解釈**
   - 書き込みはリテラルキー `attributes.{k}`。Qdrant はフィルタキーをドット区切りの
     ネストパスとして解釈し得るため、書き込み表現と不一致だと ABAC フィルタが過剰除外になる恐れ。
   - 実機 Qdrant での統合テストで確認する（本 PR では復元側を両表現対応にして安全化する）。

## 方針・変更範囲

### #1 purpose キーの統一 → `diagram-coding`

- `rag-answer` / `analysis` は「呼び出し側が送る purpose 値 ＝ `PurposeModels` の設定キー」で一致している。
  この規約に合わせ、図コード化も**呼び出し側が送る文書化済みの契約値 `diagram-coding` に設定キー側を統一**する。
  （FR-12 機能仕様・テスト仕様・コードコメントはいずれも `purpose="diagram-coding"` を契約値としている。）
- 変更: `LlmGateway.Api/appsettings.json` の `PurposeModels` キー `diagram` → `diagram-coding`。
  合わせてゲートウェイ側テスト（`LlmRouterTests` / `CompletionRoutingEndpointTests`）と
  設定コメント（`LlmRoutingOptions`）・IADR-0007 の例示を更新する。

### #2 `MapPayload` の属性復元

- `QdrantVectorStore` にペイロード → `Attributes` 復元ヘルパー（`ExtractAttributes`）を追加し、
  `MapPayload` で使用する。
- 復元は次の両表現に対応する（#3 の不確実性に対する堅牢化）:
  - (a) フラットキー `attributes.{k}`（現行の書き込み表現）。
  - (b) ネスト構造体 `attributes` → `{ k: v }`（Qdrant がドットをネストパスとして格納する場合）。
- 値は文字列（`StringValue`）を基本とし、数値/真偽値も文字列化して復元する。

### #3 フィルタキー解釈の確認・記録

- 実機 Qdrant での検証は本リポジトリのユニットテスト環境では不可。確認事項と暫定対応を
  `IADR-0014` に記録し、統合テストでの検証をフォローアップとして残す。
- 本 PR の復元ヘルパーは両表現に対応するため、実際の格納表現がどちらでも機密区分は正しく復元される。

## テスト写像（受け入れ基準）

- **#1**: `CompletionRoutingEndpointTests` の用途別モデル選択パラメタを `diagram-coding → claude-haiku-4-5`
  に更新（実 `appsettings.json` 経由のため、設定キー統一の実効ガードになる）。`LlmRouterTests` も同様。
- **#2**: `QdrantVectorStoreTests`（新規）で `ExtractAttributes` がフラットキー／ネスト構造体の双方から
  `confidentiality` 等を復元することを検証。空ペイロードでは空辞書を返す。
- **#1 回帰防止（間接）**: `LlmGatewayDiagramCoderTests` に送信 purpose が `diagram-coding` であることの
  確認を追加（設定キーと送信値の一致を担保）。

## 影響範囲

- `LlmGateway.Api/appsettings.json`, `Routing/LlmRoutingOptions.cs`（コメント）
- `RetrievalService.Api/Infrastructure/QdrantVectorStore.cs`
- テスト: `LlmGateway.Api.Tests`, `RetrievalService.Api.Tests`, `ConversionService.Worker.Tests`
- ドキュメント: `IADR-0007`（例示更新）, `IADR-0014`（新規）, `docs/tests/FR-11`（新規・写像記録）

## 非対象

- 実機 Qdrant を用いた #3 の統合テスト（フォローアップ。IADR-0014 に記録）。
- 越境マトリクス・値集合の最終確定に伴う追従（IADR-0007 のフォローアップのまま）。
