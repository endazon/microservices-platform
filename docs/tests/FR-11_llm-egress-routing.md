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
| T-02 | 用途別モデル選択 | Model 未指定時、用途に応じてモデルを切替（analysis→fable-5〔ZDR 非要件区分のみ〕 / **rag-answer→sonnet-5**〔ADR-0022 / IADR-0106〕 / **diagram-coding→haiku** / default→opus。ADR-0010 / IADR-0022） | `Model` が用途別モデル | FR-11 / `LlmRouterTests`・`CompletionRoutingEndpointTests` |
| T-03 | 送信拒否（縮退） | 許容ティアに送信可能なエンドポイントが無ければ `Sent=false` | `Sent=false`・理由に「拒否」 | FR-11 / `CompletionRoutingEndpointTests` |
| T-04 | 安全側フォールバック | 機密区分未指定・未知は restricted 相当へ倒す | `Restricted` へ写像 | FR-11 / `LlmRouterTests` |
| T-05 | **purpose キー一致（#58 #1）** | ConversionService の送信 purpose が `diagram-coding`（設定キーと一致） | リクエスト本文 `Purpose="diagram-coding"` | FR-11 / `LlmGatewayDiagramCoderTests.Sends_purpose_diagram_coding` |
| T-06 | 設定キー統一の実効ガード（#58 #1） | 実 `appsettings.json` 経由で `diagram-coding→haiku` が発火 | `Model="claude-haiku-4-5"` | FR-11 / `CompletionRoutingEndpointTests` |
| T-07 | **属性復元・フラットキー（#58 #2）** | ペイロード `attributes.{k}` を `Attributes` へ復元 | `confidentiality` 等が復元される | FR-05・FR-11 / `QdrantVectorStoreTests.ExtractAttributes_RestoresFromFlatKeys` |
| T-08 | 属性復元・ネスト構造体（#58 #2/#3） | `attributes → {k:v}` 構造体からも復元 | 復元される | FR-05・FR-11 / `QdrantVectorStoreTests.ExtractAttributes_RestoresFromNestedStruct` |
| T-09 | 属性欠落（安全側） | 属性が無いペイロードは空辞書（判定側で restricted へ縮退） | 空辞書 | FR-05 deny-by-default / `QdrantVectorStoreTests.ExtractAttributes_WhenNoAttributes_ReturnsEmpty` |
| T-10 | 既定モデル opus（ADR-0010 / ADR-0025 / IADR-0022 / IADR-0101） | 用途 `default` は既定 `claude-opus-5` を選択 | `Model="claude-opus-5"` | FR-11 / `LlmRouterTests.Route_DefaultPurpose_SelectsOpusDefaultModel` |
| T-11 | 最難関 fable-5（ADR-0010 / IADR-0022） | ZDR 非要件区分（public）の用途 `analysis` は `claude-fable-5` を選択 | `Model="claude-fable-5"` | FR-11 / `LlmRouterTests.Route_Public_Analysis_AllowsNonZdrFable5`・`CompletionRoutingEndpointTests` |
| T-12 | Copilot 経路の越境統制（ADR-0010 / IADR-0022） | Copilot（ティアC）は confidential で候補外・唯一なら送信拒否。既定無効で Claude を優先 | `Sent=false` / `Provider="claude"` | FR-11 / `LlmRouterTests.Route_Confidential_ExcludesCopilotTierC`・`Route_Public_PrefersClaudeOverDisabledCopilot` |
| T-13 | ZDR 非対応モデルの除外（IADR-0022 / IADR-0101 / 08_data-egress-policy） | ZDR 要件区分（confidential/restricted）の `analysis` は ZDR 非対応の `claude-fable-5` を除外し ZDR 対応の opus へフォールバック。明示要求でも fable-5 は不採用。ZDR 対応モデルが無ければ送信拒否 | `Model="claude-opus-5"`（fable-5 でない）/ `Sent=false` | FR-11 / `LlmRouterTests.Route_Confidential_SelectsProtectedExternalTierB`・`Route_Restricted_Analysis_ExcludesNonZdrFable5`・`Route_Confidential_IgnoresRequestedNonZdrModel`・`Route_Confidential_WhenAllModelsNonZdr_IsDenied`・`CompletionRoutingEndpointTests.PostComplete_ConfidentialAnalysis_FallsBackToZdrModel` |
| T-14 | 既定 max_tokens（IADR-0101） | `maxTokens` を省略した `/complete` 要求は、共有契約 `CompletionApiRequest` の既定 4096 をプロバイダへ渡す。thinking が既定有効なモデルでは `max_tokens` が思考＋本文の合算上限になるため、1024 へ戻る回帰を防ぐ | プロバイダ受領値 `MaxTokens=4096` | FR-11 / `CompletionRoutingEndpointTests.PostComplete_WithoutMaxTokens_PassesContractDefaultToProvider` |
| T-15 | 取引判断のモデルピン留め（AST/ADR-0011 / IADR-0102） | 用途 `trade-decision` はピン留めした `claude-opus-4-8` を選択し、既定（`claude-opus-5`）へ落ちない。ZDR 要件区分（confidential）でも維持される（Opus 4.8 は `NonZdrModels` 対象外）。一方 `report-narrative` は AST/ADR-0011 §決定により `default` に着地する | `Model="claude-opus-4-8"`（`claude-opus-5` でない）/ report-narrative は `Model="claude-opus-5"` | FR-11 / `LlmRouterTests.Route_TradeDecision_PinsOpus48AndDoesNotFollowDefault`・`Route_Confidential_TradeDecision_KeepsPinnedOpus48`・`Route_ReportNarrative_FollowsDefaultModel` |
| T-16 | **拒否の判別（ADR-0025 / IADR-0104 / #379）** | `stop_reason="refusal"` を「送信したが空応答」と区別する。本文なし／**部分本文あり**のいずれでも本文を破棄し、理由を応答契約（`stopReason`）と warn ログへ残す。`Sent` は `true` のまま（越境は成立している）。SSE でも `done` イベントに載る | `StopReason="refusal"`・`Text=""`・`sent=true`／SSE `"stopReason":"refusal"` | FR-11 / `ClaudeProviderStopReasonTests.CompleteAsync_WhenRefusalWithoutBody_ReportsRefusalStopReason`・`CompleteAsync_WhenRefusalWithPartialBody_DiscardsBody`・`StreamAsync_WhenRefusal_FinalChunkCarriesRefusal`・`CompletionStopReasonEndpointTests.PostComplete_WhenProviderRefuses_ReportsRefusalAndKeepsSentTrue`・`PostCompleteStream_WhenProviderRefuses_DoneEventCarriesRefusal`・`RagOrchestratorStopReasonTests.AskAsync_WhenGatewayReportsRefusal_ReturnsRefusalMessage`・`AskStreamAsync_WhenDoneReportsRefusal_EmitsRefusalToken`・`AskStreamAsync_WhenRefusedWithoutDeltas_EmitsNoticeWithoutLeadingBlankLine`・`AskStreamAsync_WhenRefusedAfterDeltas_SeparatesNoticeFromBody`（注記の表示分離）・`LlmGatewayDiagramCoderTests.Retains_with_refusal_reason_when_model_refuses`（FR-12 経路も `not-codeable` と誤記録しない） |
| T-17 | **上限到達の判別（IADR-0101 / IADR-0104 / #379）** | `stop_reason="max_tokens"`（thinking が上限を食い切ると本文が空になり得る）を拒否と区別する。途中結果は**破棄しない**（IADR-0101 が記録した劣化の観測対象） | `StopReason="max_tokens"`・`IsRefusal=false`・途中本文は保持 | FR-11 / `ClaudeProviderStopReasonTests.CompleteAsync_WhenMaxTokens_IsDistinguishableFromRefusal`・`CompleteAsync_WhenMaxTokens_KeepsTruncatedBody`・`CompletionStopReasonEndpointTests.PostComplete_WhenMaxTokens_ReportsMaxTokensStopReason` |
| T-18 | 正常終了と egress 拒否の不変性（IADR-0104 / #379） | `end_turn` では本文・トークン数が従来どおりで `stopReason` が透過する。egress 拒否（未送信）は従来どおり `sent=false` で `stopReason` は付かない（「送っていない」と「送ったが拒否された」を取り違えない）。未知の `stop_reason` は enum へ丸めず透過する | `StopReason="end_turn"`・本文/トークン数不変／`sent=false` かつ `stopReason=null`／未知値はそのまま | FR-11 / `ClaudeProviderStopReasonTests.CompleteAsync_WhenEndTurn_ReturnsBodyAndStopReason`・`StreamAsync_WhenEndTurn_StreamsDeltasAndCarriesStopReason`・`CompleteAsync_WhenUnknownStopReason_PassesItThrough`・`CompletionStopReasonEndpointTests.PostComplete_WhenEndTurn_ReturnsBodyWithStopReason`・`PostComplete_WhenEgressDenied_HasNoStopReason`・`RagOrchestratorStopReasonTests.AskAsync_WhenEndTurn_ReturnsBodyUnchanged` |
| T-19 | **定型 RAG 回答の Sonnet 5 追随と許可集合ガード（ADR-0022 / IADR-0106 / #381）** | 用途 `rag-answer` は `claude-sonnet-5` を選択し、`DefaultModel`（`claude-opus-5`）へ落ちない。ZDR 要件区分（confidential/restricted）でも Sonnet 5 は ZDR 対応のため除外されず維持される。あわせて **`PurposeModels` の全値が claude エンドポイントの `Models`（利用許可集合）に含まれる**ことを固定する（未登録だと `ResolveModel` が無音で `DefaultModel` へフォールバックし割当が失効する。#376 / IADR-0102 で実際に踏んだ罠） | `Model="claude-sonnet-5"`（`claude-opus-5` でない）/ 全 `PurposeModels` 値 ⊆ `Models` | FR-11 / `LlmRouterTests.Route_RagAnswer_PinsSonnet5AndDoesNotFallBackToDefault`・`Route_Restricted_RagAnswer_KeepsSonnet5`・`Route_Confidential_IgnoresRequestedNonZdrModel`・`CompletionRoutingEndpointTests.PostComplete_RagAnswer_SelectsSonnet5AndDoesNotFallBackToDefault`・`PurposeModels_AreAllRegisteredInClaudeEndpointModels`・`PostComplete_WithoutExplicitModel_SelectsPurposeModel` |
| T-20 | **OpenAI 互換 `finish_reason` の正規化（IADR-0109 / #394）** | ティアA（`SelfHostedProvider`）・ティアC（`CopilotProvider`）が `choices[].finish_reason` を読み、正準語彙へ写像する。`stop`→`end_turn` / `length`→`max_tokens` / `content_filter`→`refusal` / `tool_calls`・`function_call`→`tool_use`（大小文字非依存）。**未知値は既定値へ潰さず原文透過し warn ログ**に残る。欠落・null は `StopReason=null` で warn を出さない。`content_filter` は本文破棄・`length` は途中結果保持（IADR-0104 と一貫）。既定 `StreamAsync`（IADR-0037）でも最終チャンクへ載る | `StopReason` が正準語彙／未知値は原文／`content_filter` で `Text=""` | FR-11 / `OpenAiFinishReasonTests`（写像表・未知値・大小文字・欠落）・`OpenAiProviderStopReasonTests`（両プロバイダの代表値・本文破棄/保持・warn ログ・既定 StreamAsync） |
| T-21 | **終了理由のメトリクス（IADR-0110 / #395）** | 補完 1 回ごとに `llm.completion.total` を計上する。拒否＝`result=sent`＋`stop_reason=refusal`／上限到達＝`max_tokens`／正常終了＝`end_turn`／越境拒否＝`result=egress_denied`＋`stop_reason=none`／呼び出し失敗＝`result=upstream_error`。**未知の終了理由と未定義の purpose は `other` へ集約**（カーディナリティを閉じる）。ストリーミング経路も同じ属性で計上 | `MeterListener` が捕捉した測定の属性が上記どおり（1 リクエスト＝1 計上） | FR-11 / NFR / `CompletionMetricsTests`（拒否・上限到達・正常終了・未知理由・未定義 purpose・越境拒否・呼び出し失敗・SSE の 8 ケース） |

## 未確認・フォローアップ（#58 #3）

- Qdrant のフィルタキー（`attributes.{k}`）のドット解釈（リテラル or ネストパス）は**実機 Qdrant の
  統合テストで確認**する。過剰除外が確認された場合は書き込み・フィルタ・復元をネスト構造体へ統一する。
  詳細は [IADR-0014](../adr/IADR-0014_qdrant-attribute-payload-key.md) を参照。
- 本 PR の復元ヘルパー（`ExtractAttributes`）は両表現に対応するため、実際の格納表現がどちらでも
  機密区分は正しく復元される（T-07/T-08）。
