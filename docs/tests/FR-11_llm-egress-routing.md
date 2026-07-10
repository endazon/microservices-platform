---
title: テスト仕様書 — FR-11 用途別・機密度別 LLM ルーティング
type: test-spec
status: completed
related_ids:
  - FR-11
  - FR-05
  - FR-02
  - UC-02
author: claude
created: 2026-07-04
updated: 2026-07-06
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-11, FR-05)"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md (UC-02)"
  - "../../planning/projects/microservices-platform/06_technical/08_data-egress-policy.md"
related_specs:
  - ../specs/20260702_FR-11_llm-egress-routing.md
  - ../specs/20260704_FR-11_llm-routing-runtime-fixes.md
related_adrs:
  - ../adr/IADR-0007_llm-egress-routing-config-driven.md
  - ../adr/IADR-0014_qdrant-attribute-payload-key.md
---

# テスト仕様書: FR-11 用途別・機密度別 LLM ルーティング

## 対象

- `src/platform/backend/Services/LlmGateway/tests/LlmGateway.Api.Tests`
- `src/knowledge/backend/Services/RetrievalService/tests/RetrievalService.Api.Tests`
- `src/knowledge/backend/Services/ConversionService/tests/ConversionService.Worker.Tests`

## テストケース（受け入れ基準・実運用不具合の写像）

| ID | 観点 | 内容 | 期待 | 起点 |
| --- | --- | --- | --- | --- |
| T-01 | 越境マトリクス | 機密区分→許容ティアが 08_data-egress-policy に一致 | `AllowedTiers` が表どおり | FR-11 / `LlmRouterTests` |
| T-02 | 用途別モデル選択 | Model 未指定時、用途に応じてモデルを切替（analysis→fable-5〔ZDR 非要件区分のみ〕 / rag-answer→sonnet / **diagram-coding→haiku** / default→opus。ADR-0010 / IADR-0022） | `Model` が用途別モデル | FR-11 / `LlmRouterTests`・`CompletionRoutingEndpointTests` |
| T-03 | 送信拒否（縮退） | 許容ティアに送信可能なエンドポイントが無ければ `Sent=false` | `Sent=false`・理由に「拒否」 | FR-11 / `CompletionRoutingEndpointTests` |
| T-04 | 安全側フォールバック | 機密区分未指定・未知は restricted 相当へ倒す | `Restricted` へ写像 | FR-11 / `LlmRouterTests` |
| T-05 | **purpose キー一致（#58 #1）** | ConversionService の送信 purpose が `diagram-coding`（設定キーと一致） | リクエスト本文 `Purpose="diagram-coding"` | FR-11 / `LlmGatewayDiagramCoderTests.Sends_purpose_diagram_coding` |
| T-06 | 設定キー統一の実効ガード（#58 #1） | 実 `appsettings.json` 経由で `diagram-coding→haiku` が発火 | `Model="claude-haiku-4-5"` | FR-11 / `CompletionRoutingEndpointTests` |
| T-07 | **属性復元・フラットキー（#58 #2）** | ペイロード `attributes.{k}` を `Attributes` へ復元 | `confidentiality` 等が復元される | FR-05・FR-11 / `QdrantVectorStoreTests.ExtractAttributes_RestoresFromFlatKeys` |
| T-08 | 属性復元・ネスト構造体（#58 #2/#3） | `attributes → {k:v}` 構造体からも復元 | 復元される | FR-05・FR-11 / `QdrantVectorStoreTests.ExtractAttributes_RestoresFromNestedStruct` |
| T-09 | 属性欠落（安全側） | 属性が無いペイロードは空辞書（判定側で restricted へ縮退） | 空辞書 | FR-05 deny-by-default / `QdrantVectorStoreTests.ExtractAttributes_WhenNoAttributes_ReturnsEmpty` |
| T-10 | 既定モデル opus（ADR-0010 / IADR-0022） | 用途 `default` は既定 `claude-opus-4-8` を選択 | `Model="claude-opus-4-8"` | FR-11 / `LlmRouterTests.Route_DefaultPurpose_SelectsOpusDefaultModel` |
| T-11 | 最難関 fable-5（ADR-0010 / IADR-0022） | ZDR 非要件区分（public）の用途 `analysis` は `claude-fable-5` を選択 | `Model="claude-fable-5"` | FR-11 / `LlmRouterTests.Route_Public_Analysis_AllowsNonZdrFable5`・`CompletionRoutingEndpointTests` |
| T-12 | Copilot 経路の越境統制（ADR-0010 / IADR-0022） | Copilot（ティアC）は confidential で候補外・唯一なら送信拒否。既定無効で Claude を優先 | `Sent=false` / `Provider="claude"` | FR-11 / `LlmRouterTests.Route_Confidential_ExcludesCopilotTierC`・`Route_Public_PrefersClaudeOverDisabledCopilot` |
| T-13 | ZDR 非対応モデルの除外（IADR-0022 / 08_data-egress-policy） | ZDR 要件区分（confidential/restricted）の `analysis` は ZDR 非対応の `claude-fable-5` を除外し ZDR 対応の opus へフォールバック。明示要求でも fable-5 は不採用。ZDR 対応モデルが無ければ送信拒否 | `Model="claude-opus-4-8"`（fable-5 でない）/ `Sent=false` | FR-11 / `LlmRouterTests.Route_Confidential_SelectsProtectedExternalTierB`・`Route_Restricted_Analysis_ExcludesNonZdrFable5`・`Route_Confidential_IgnoresRequestedNonZdrModel`・`Route_Confidential_WhenAllModelsNonZdr_IsDenied`・`CompletionRoutingEndpointTests.PostComplete_ConfidentialAnalysis_FallsBackToZdrModel` |

## 未確認・フォローアップ（#58 #3）

- Qdrant のフィルタキー（`attributes.{k}`）のドット解釈（リテラル or ネストパス）は**実機 Qdrant の
  統合テストで確認**する。過剰除外が確認された場合は書き込み・フィルタ・復元をネスト構造体へ統一する。
  詳細は [IADR-0014](../adr/IADR-0014_qdrant-attribute-payload-key.md) を参照。
- 本 PR の復元ヘルパー（`ExtractAttributes`）は両表現に対応するため、実際の格納表現がどちらでも
  機密区分は正しく復元される（T-07/T-08）。
