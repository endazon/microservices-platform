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
updated: 2026-08-18
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
| T-02 | 用途別モデル選択 | Model 未指定時、用途に応じてモデルを切替（**analysis→opus-5**〔#850・計画 ADR-0038 決定 1 で fable-5 から改定。既定と同値になるため、この行だけでは用途別割当の発火と DefaultModel への無音フォールバックを区別できない —— 区別は T-19 と合成 config 側が担う〕 / **rag-answer→sonnet-5**〔ADR-0022 / IADR-0106〕 / **diagram-coding→haiku** / default→opus。ADR-0010 / IADR-0022） | `Model` が用途別モデル | FR-11 / `LlmRouterTests`・`CompletionRoutingEndpointTests` |
| T-03 | 送信拒否（縮退） | 許容ティアに送信可能なエンドポイントが無ければ `Sent=false` | `Sent=false`・理由に「拒否」 | FR-11 / `CompletionRoutingEndpointTests` |
| T-04 | 安全側フォールバック | 機密区分未指定・未知は restricted 相当へ倒す | `Restricted` へ写像 | FR-11 / `LlmRouterTests` |
| T-05 | **purpose キー一致（#58 #1）** | ConversionService の送信 purpose が `diagram-coding`（設定キーと一致） | リクエスト本文 `Purpose="diagram-coding"` | FR-11 / `LlmGatewayDiagramCoderTests.Sends_purpose_diagram_coding` |
| T-06 | 設定キー統一の実効ガード（#58 #1） | 実 `appsettings.json` 経由で `diagram-coding→haiku` が発火 | `Model="claude-haiku-4-5"` | FR-11 / `CompletionRoutingEndpointTests` |
| T-07 | **属性復元・フラットキー（#58 #2）** | ペイロード `attributes.{k}` を `Attributes` へ復元 | `confidentiality` 等が復元される | FR-05・FR-11 / `QdrantVectorStoreTests.ExtractAttributes_RestoresFromFlatKeys` |
| T-08 | 属性復元・ネスト構造体（#58 #2/#3） | `attributes → {k:v}` 構造体からも復元 | 復元される | FR-05・FR-11 / `QdrantVectorStoreTests.ExtractAttributes_RestoresFromNestedStruct` |
| T-09 | 属性欠落（安全側） | 属性が無いペイロードは空辞書（判定側で restricted へ縮退） | 空辞書 | FR-05 deny-by-default / `QdrantVectorStoreTests.ExtractAttributes_WhenNoAttributes_ReturnsEmpty` |
| T-10 | 既定モデル opus（ADR-0010 / ADR-0025 / IADR-0022 / IADR-0101） | 用途 `default` は既定 `claude-opus-5` を選択 | `Model="claude-opus-5"` | FR-11 / `LlmRouterTests.Route_DefaultPurpose_SelectsOpusDefaultModel` |
| T-11 | **非 ZDR モデルは ZDR 非要件区分でのみ選択される（IADR-0022）**。**［2026-08-18 更新 / #850・計画 ADR-0038］本番設定の検証ではなくなった** | **合成 config**（`NonZdrModels` に非 ZDR モデルを持つエンドポイント）において、ZDR 非要件区分（public）の用途 `analysis` はその非 ZDR モデルを選択できる。**本番設定では `analysis`=`claude-opus-5` / `NonZdrModels` は空であり、この経路は発火しない** —— テスト名の `Fable5` は**合成 config が置く値**であって現行の割当ではない。**それでも残す**: 除外機構が「ZDR 要件区分でだけ効く」ことを示す対の片方であり、消すと T-13 が何と対比しているのか読めなくなる | 合成 config 上で `Model="claude-fable-5"` | FR-11 / `LlmRouterTests.Route_Public_Analysis_AllowsNonZdrFable5`（**合成 config のみ**。`CompletionRoutingEndpointTests` 側の対応ケースは #850 で `analysis→claude-opus-5` へ改めた） |
| T-12 | Copilot 経路の越境統制（ADR-0010 / IADR-0022） | Copilot（ティアC）は confidential で候補外・唯一なら送信拒否。既定無効で Claude を優先 | `Sent=false` / `Provider="claude"` | FR-11 / `LlmRouterTests.Route_Confidential_ExcludesCopilotTierC`・`Route_Public_PrefersClaudeOverDisabledCopilot` |
| T-13 | ZDR 非対応モデルの除外（IADR-0022 / IADR-0101 / 08_data-egress-policy） | ZDR 要件区分（confidential/restricted）では `NonZdrModels` に載るモデルを除外し ZDR 対応モデルへフォールバック。明示要求でも不採用。ZDR 対応モデルが無ければ送信拒否。**［2026-08-18 更新 / #850・計画 ADR-0038］本番設定の `NonZdrModels` は空になり、この除外は本番経路では発火しない。機構の検証は `LlmRouterTests` の合成 config が担う** —— #850 で**変異試験**（`LlmRouterTests.cs` の `NonZdrModels = ["claude-fable-5"]` を**3 箇所すべて**——30 行の共有ヘルパ `Claude()`・253 行・278 行——から外す）を行い、下記 5 本すべてが除外の分岐に依存して緑になっていること（空振りしていないこと）を実測した。**［2026-08-18 更新 / #859］条件を「3 箇所すべて」と明記した** —— 共有ヘルパの 1 箇所だけを空にすると実測 3 本しか落ちず（253 / 278 行を使うテストは自前の合成 config で除外分岐を通り続ける）、手順どおり追試しても 5 本を再現できなかったためである | 合成 config 上で `Model="claude-opus-5"`（fable-5 でない）/ `Sent=false` | FR-11 / `LlmRouterTests.Route_Confidential_SelectsProtectedExternalTierB`・`Route_Restricted_Analysis_ExcludesNonZdrFable5`・`Route_Confidential_IgnoresRequestedNonZdrModel`・`Route_Confidential_WhenAllModelsNonZdr_IsDenied`・`Route_Confidential_FallsBackToNextCandidateWhenLeadHasNoZdrModel`・`CompletionRoutingEndpointTests.PostComplete_ConfidentialAnalysis_ResolvesZdrModel`（#850 で `...FallsBackToZdrModel` から改名） |
| T-14 | 既定 max_tokens（IADR-0101） | `maxTokens` を省略した `/complete` 要求は、共有契約 `CompletionApiRequest` の既定 4096 をプロバイダへ渡す。thinking が既定有効なモデルでは `max_tokens` が思考＋本文の合算上限になるため、1024 へ戻る回帰を防ぐ | プロバイダ受領値 `MaxTokens=4096` | FR-11 / `CompletionRoutingEndpointTests.PostComplete_WithoutMaxTokens_PassesContractDefaultToProvider` |
| T-15 | 取引判断のモデルピン留め（AST/ADR-0011 / IADR-0102 / IADR-0112） | 用途 `trade-decision` はピン留めした `claude-sonnet-5` を選択し、既定（`claude-opus-5`）へ落ちない。ZDR 要件区分（confidential）でも維持される（Sonnet 5 は `NonZdrModels` 対象外）。旧ピン `claude-opus-4-8` が残っていないことも固定する（IADR-0112 決定3 でピンの値を改定。固定する仕組みは維持）。一方 `report-narrative` はエントリを持たず `default` に着地する（呼び出し側の移行が完了するまでの非破壊性のため維持・IADR-0112 決定1） | `Model="claude-sonnet-5"`（`claude-opus-5` でも `claude-opus-4-8` でもない）/ report-narrative は `Model="claude-opus-5"` | FR-11 / `LlmRouterTests.Route_TradeDecision_PinsSonnet5AndDoesNotFollowDefault`・`Route_Confidential_TradeDecision_KeepsPinnedSonnet5`・`Route_ReportNarrative_FollowsDefaultModel`・`CompletionRoutingEndpointTests.PostComplete_TradeDecision_SelectsSonnet5AndDoesNotFallBackToDefault` |
| T-16 | **拒否の判別（ADR-0025 / IADR-0104 / #379）** | `stop_reason="refusal"` を「送信したが空応答」と区別する。本文なし／**部分本文あり**のいずれでも本文を破棄し、理由を応答契約（`stopReason`）と warn ログへ残す。`Sent` は `true` のまま（越境は成立している）。SSE でも `done` イベントに載る | `StopReason="refusal"`・`Text=""`・`sent=true`／SSE `"stopReason":"refusal"` | FR-11 / `ClaudeProviderStopReasonTests.CompleteAsync_WhenRefusalWithoutBody_ReportsRefusalStopReason`・`CompleteAsync_WhenRefusalWithPartialBody_DiscardsBody`・`StreamAsync_WhenRefusal_FinalChunkCarriesRefusal`・`CompletionStopReasonEndpointTests.PostComplete_WhenProviderRefuses_ReportsRefusalAndKeepsSentTrue`・`PostCompleteStream_WhenProviderRefuses_DoneEventCarriesRefusal`・`RagOrchestratorStopReasonTests.AskAsync_WhenGatewayReportsRefusal_ReturnsRefusalMessage`・`AskStreamAsync_WhenDoneReportsRefusal_EmitsRefusalToken`・`AskStreamAsync_WhenRefusedWithoutDeltas_EmitsNoticeWithoutLeadingBlankLine`・`AskStreamAsync_WhenRefusedAfterDeltas_SeparatesNoticeFromBody`（注記の表示分離）・`LlmGatewayDiagramCoderTests.Retains_with_refusal_reason_when_model_refuses`（FR-12 経路も `not-codeable` と誤記録しない） |
| T-17 | **上限到達の判別（IADR-0101 / IADR-0104 / #379）** | `stop_reason="max_tokens"`（thinking が上限を食い切ると本文が空になり得る）を拒否と区別する。途中結果は**破棄しない**（IADR-0101 が記録した劣化の観測対象） | `StopReason="max_tokens"`・`IsRefusal=false`・途中本文は保持 | FR-11 / `ClaudeProviderStopReasonTests.CompleteAsync_WhenMaxTokens_IsDistinguishableFromRefusal`・`CompleteAsync_WhenMaxTokens_KeepsTruncatedBody`・`CompletionStopReasonEndpointTests.PostComplete_WhenMaxTokens_ReportsMaxTokensStopReason` |
| T-18 | 正常終了と egress 拒否の不変性（IADR-0104 / #379） | `end_turn` では本文・トークン数が従来どおりで `stopReason` が透過する。egress 拒否（未送信）は従来どおり `sent=false` で `stopReason` は付かない（「送っていない」と「送ったが拒否された」を取り違えない）。未知の `stop_reason` は enum へ丸めず透過する | `StopReason="end_turn"`・本文/トークン数不変／`sent=false` かつ `stopReason=null`／未知値はそのまま | FR-11 / `ClaudeProviderStopReasonTests.CompleteAsync_WhenEndTurn_ReturnsBodyAndStopReason`・`StreamAsync_WhenEndTurn_StreamsDeltasAndCarriesStopReason`・`CompleteAsync_WhenUnknownStopReason_PassesItThrough`・`CompletionStopReasonEndpointTests.PostComplete_WhenEndTurn_ReturnsBodyWithStopReason`・`PostComplete_WhenEgressDenied_HasNoStopReason`・`RagOrchestratorStopReasonTests.AskAsync_WhenEndTurn_ReturnsBodyUnchanged` |
| T-19 | **定型 RAG 回答の Sonnet 5 追随と許可集合ガード（ADR-0022 / IADR-0106 / #381）** | 用途 `rag-answer` は `claude-sonnet-5` を選択し、`DefaultModel`（`claude-opus-5`）へ落ちない。ZDR 要件区分（confidential/restricted）でも Sonnet 5 は ZDR 対応のため除外されず維持される。あわせて **`PurposeModels` の全値が claude エンドポイントの `Models`（利用許可集合）に含まれる**ことを固定する（未登録だと `ResolveModel` が無音で `DefaultModel` へフォールバックし割当が失効する。#376 / IADR-0102 で実際に踏んだ罠） | `Model="claude-sonnet-5"`（`claude-opus-5` でない）/ 全 `PurposeModels` 値 ⊆ `Models` | FR-11 / `LlmRouterTests.Route_RagAnswer_PinsSonnet5AndDoesNotFallBackToDefault`・`Route_Restricted_RagAnswer_KeepsSonnet5`・`Route_Confidential_IgnoresRequestedNonZdrModel`・`CompletionRoutingEndpointTests.PostComplete_RagAnswer_SelectsSonnet5AndDoesNotFallBackToDefault`・`PurposeModels_AreAllRegisteredInClaudeEndpointModels`・`PostComplete_WithoutExplicitModel_SelectsPurposeModel` |
| T-20 | **OpenAI 互換 `finish_reason` の正規化（IADR-0109 / #394）** | ティアA（`SelfHostedProvider`）・ティアC（`CopilotProvider`）が `choices[].finish_reason` を読み、正準語彙へ写像する。`stop`→`end_turn` / `length`→`max_tokens` / `content_filter`→`refusal` / `tool_calls`・`function_call`→`tool_use`（大小文字非依存）。**未知値は既定値へ潰さず原文透過し warn ログ**に残る。欠落・null は `StopReason=null` で warn を出さない。`content_filter` は本文破棄・`length` は途中結果保持（IADR-0104 と一貫）。既定 `StreamAsync`（IADR-0037）でも最終チャンクへ載る | `StopReason` が正準語彙／未知値は原文／`content_filter` で `Text=""` | FR-11 / `OpenAiFinishReasonTests`（写像表・未知値・大小文字・欠落）・`OpenAiProviderStopReasonTests`（両プロバイダの代表値・本文破棄/保持・warn ログ・既定 StreamAsync） |
| T-21 | **終了理由のメトリクス（IADR-0110 / #395）** | 補完 1 回ごとに `llm.completion.total` を計上する。拒否＝`result=sent`＋`stop_reason=refusal`／上限到達＝`max_tokens`／正常終了＝`end_turn`／越境拒否＝`result=egress_denied`＋`stop_reason=none`／呼び出し失敗＝`result=upstream_error`。**未知の終了理由と未定義の purpose は `other` へ集約**（カーディナリティを閉じる）。ストリーミング経路も同じ属性で計上 | `MeterListener` が捕捉した測定の属性が上記どおり（1 リクエスト＝1 計上） | FR-11 / NFR / `CompletionMetricsTests`（拒否・上限到達・正常終了・未知理由・未定義 purpose・越境拒否・呼び出し失敗・SSE の 8 ケース） |
| T-22 | **報告書の種別別モデルと取引判断の改定（IADR-0112 / IADR-0113 / #420 #421 / AST#309）** | 報告書は方針階層（月報→週報→日報→取引。`AST/04_workflows/03_reporting-cycle`）をなす方針書であり、種別ごとに用途を分けて解決する（`report-monthly→claude-opus-5`〔IADR-0113 で `claude-fable-5` から改定〕 / `report-weekly→claude-opus-5` / `report-daily→claude-sonnet-5`）。3 種別が 1 モデルへ潰れない（日報は別モデル）。`report-weekly` は `default` と同値だが明示エントリで固定する（無いと `default` 改定で無音に失効する）。月報も週報と同値になったが、同じ理由で明示エントリを残す | 各用途が指定モデルを返す（検証区分は report-service の実運用値 `internal`） | FR-11 / `LlmRouterTests.Route_ReportKindPurpose_ResolvesKindSpecificModel`・`CompletionRoutingEndpointTests.PostComplete_ReportKindPurpose_SelectsKindSpecificModel` |
| T-23 | **報告書の割当は機密区分で変わらない（IADR-0113 / AST#309）** | 旧割当 `claude-fable-5` は `NonZdrModels` に載る唯一の非 ZDR モデルであり、`confidential` 以上では `EligibleModels` から除外され `DefaultModel` へ**黙って**落ちていた。ZDR 対応モデルへ改定したことで、`report-*` の割当が呼び出し側の機密区分設定（report-service の `LlmGateway:Confidentiality`。既定 `internal`）に左右されないことを固定する。あわせて **割当モデルが `NonZdrModels` に含まれない**ことを集合として固定し、用途追加時の再発を防ぐ（T-19 と同じ発想の設定ガード）。**［2026-08-18 更新 / #850・計画 ADR-0038］射程を `report-*` から全 `PurposeModels` へ広げた** —— 旧: 「`analysis` は ZDR 非要件区分限定の意図的な例外〔IADR-0022〕のため対象外」。同 ADR 決定 2 でその例外が消滅したため、絞る理由が無くなった（IADR-0113 決定 4 の射程の改定にあたる旨は同 IADR の同日追記に記録）。**広げたことが効いていることは変異試験で実測した** —— `NonZdrModels` に `claude-haiku-4-5`（`diagram-coding` だけが使う＝旧射程では捕まらない）を入れると、157 本中**本ガード 1 本だけ**が落ちる | `internal`/`confidential`/`restricted` のいずれでも `Sent=true` かつ `Model="claude-opus-5"`（`claude-fable-5` でない）/ 全 `PurposeModels` 割当 ∉ `NonZdrModels` | FR-11 / `LlmRouterTests.Route_ReportKindPurpose_ResolvesSameModelAcrossSensitivities`・`CompletionRoutingEndpointTests.PostComplete_ReportMonthly_KeepsAssignedModelAcrossSensitivities`・`PurposeModels_AreNotListedAsNonZdr`（#850 で `ReportPurposeModels_...` から改名・射程拡大） |
| T-24 | **未知 content ブロックで応答全体を失わない（IADR-0114 / AST#290）** | Anthropic.SDK 4.0.0 の content 判別子は `text`/`image`/`tool_use`/`tool_result` の 4 種しか知らず、`thinking`（Opus 5 / Sonnet 5 は既定有効・Fable 5 は常時有効）が 1 個混ざるだけで `JsonException: Unknown type thinking` により**応答全体**を失う。**既知型の許可リスト**で未知型（`thinking` / `redacted_thinking` / `server_tool_use` / 将来型）を除去し、本文テキスト・構造化出力・トークン数・`stopReason` を従来どおり取得する。既知型のみの応答は書き換えない／全ブロック未知なら content 空へ縮退（例外にしない）／非 2xx と SSE は素通し／除去した型名は WARN に残す。`refusal` の本文破棄（IADR-0104）は不変 | `CompleteAsync` が例外を投げず本文を返す。取引判断 JSON の `action` が Buy/Sell/Hold として読める。**変異テスト**: サニタイズを外すと同応答で `Unknown type thinking` になる | FR-11 / FR-04 / `AnthropicContentBlockSanitizerTests`（許可リスト・将来型・素通し・空縮退・非 JSON・非 2xx・SSE の 11 ケース）・`ClaudeProviderThinkingTests`（本文取得・Buy/Sell/Hold・未知型・変異・素通し・refusal の 8 ケース） |

## 未確認・フォローアップ（#58 #3）

- Qdrant のフィルタキー（`attributes.{k}`）のドット解釈（リテラル or ネストパス）は**実機 Qdrant の
  統合テストで確認**する。過剰除外が確認された場合は書き込み・フィルタ・復元をネスト構造体へ統一する。
  詳細は [IADR-0014](../adr/IADR-0014_qdrant-attribute-payload-key.md) を参照。
- 本 PR の復元ヘルパー（`ExtractAttributes`）は両表現に対応するため、実際の格納表現がどちらでも
  機密区分は正しく復元される（T-07/T-08）。
