---
title: 作業仕様書 — ADR-0010 (b)実装追従: 既定モデル変更・fable-5/Copilot 経路の追加
type: spec
status: completed
related_ids:
  - ADR-0010
  - IADR-0007
  - IADR-0022
  - FR-11
  - UC-02
author: claude
created: 2026-07-06
updated: 2026-07-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md (Accepted)
  - planning:draft/feedback/20260706_adr-0010-model-decision-b.md (triage: accepted)
  - planning:projects/microservices-platform/06_technical/04_ai-rag-stack.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
related_specs:
  - ./20260702_FR-11_llm-egress-routing.md
  - ./20260704_FR-11_llm-routing-runtime-fixes.md
related_adrs:
  - IADR-0022 (既定モデル変更・fable-5/Copilot 経路の追加)
  - IADR-0007 (config 駆動 LLM ルーティング)
  - ADR-0010 (外部マネージドAPI主体のLLMゲートウェイ)
---

# 作業仕様書: ADR-0010 (b)実装追従 — 既定モデル変更・fable-5/Copilot 経路の追加

親 Issue: #69（起点 #57 / #48）。トリガー: 計画側 ADR-0010 の (b) 実装追従が `Accepted` で確定。

## 目的

ADR-0010（`Accepted`）の決定文に実装を追従させる。計画側判断は **(b) 実装追従**（トリアージ記録
`draft/feedback/20260706_adr-0010-model-decision-b.md` = accepted）。

- 既定 = `claude-opus-4-8`
- 定型 = `claude-sonnet-4-6` / `claude-haiku-4-5`
- 最難関 = `claude-fable-5` ／ GitHub Copilot SDK

実装は IADR-0007（設定駆動のエンドポイント定義＋越境マトリクス）を維持したまま、既定モデル・
用途別モデル・プロバイダ経路を **設定とプロバイダ追加のみ** で追従する（越境マトリクス `EgressMatrix`
のロジックは変更しない）。

## 背景・現状

- 実装側の既定は `claude-sonnet-4-6`（`Llm:Model` / `Llm:DefaultModel`）、用途別は `analysis→opus` /
  `rag-answer→sonnet` / `diagram-coding→haiku`。`claude-fable-5`・GitHub Copilot SDK は未実装だった。
- Issue #69 で実装側は当初 (a)（実態追認）を推奨したが、計画側は (b) を採用して ADR-0010 を確定。

## 受け入れ基準

1. グローバル既定モデルが `claude-opus-4-8` になる（`Llm:Model` / `Llm:DefaultModel` / コードのフォールバック）。
2. 用途別モデルが「既定 opus / 定型 sonnet・haiku / 最難関 analysis→fable-5」になる。
   - `default→claude-opus-4-8`, `rag-answer→claude-sonnet-4-6`, `diagram-coding→claude-haiku-4-5`,
     `analysis→claude-fable-5`。
3. `claude-fable-5` が claude-managed（ティアB）エンドポイントの `Models` に含まれ、ZDR 非要件の区分
   （public/internal）の `analysis` で選択される。
   - **ZDR 非対応対応**: 08_data-egress-policy の注意点（fable-5 は ZDR 非対応）に従い、`NonZdrModels` に
     `claude-fable-5` を列挙する。`EgressMatrix.RequiresZeroDataRetention` が真の区分（confidential/restricted、
     未知区分も安全側で真）では `LlmRouter` が fable-5 を候補から除外し、ZDR 対応の既定モデル（opus）へ
     フォールバックする。適格モデルが無ければ送信拒否へ縮退する。
4. GitHub Copilot 用の `ILlmProvider` 実装（`CopilotProvider`）が追加され、キー付き DI（`copilot`）で登録される。
5. Copilot エンドポイントが設定に定義される。**送信先ティア（08_data-egress-policy の契約条件）が
   未確定のため、安全側でティアC・既定 `Enabled=false`** とする（selfhosted と同じ後付けパターン）。
   確定後に設定で有効化・ティア再判定する（IADR-0022 のフォローアップ）。
6. 越境マトリクス（`EgressMatrix`）は既存のティア判定でそのまま Copilot（ティアC）を統制する
   （confidential/restricted はティアC不可、internal×C は要承認）。ロジック変更なし。
7. ルーティング・用途別モデル選択のテストが green（既存 + 追加）。
8. `/verify`（build / test / lint）が green。

## 変更範囲

- `src/Services/LlmGateway/src/LlmGateway.Api/appsettings.json`: 既定モデル・PurposeModels・Endpoints・Copilot 設定。
- `src/Services/LlmGateway/src/LlmGateway.Api/Providers/ClaudeProvider.cs`: 既定フォールバック opus。
- `src/Services/LlmGateway/src/LlmGateway.Api/Providers/CopilotProvider.cs`: 新規（GitHub Copilot 経路）。
- `src/Services/LlmGateway/src/LlmGateway.Api/Program.cs`: `copilot` キー付き DI 登録。
- `src/Services/LlmGateway/src/LlmGateway.Api/Routing/LlmRoutingOptions.cs`: コメント更新 / `NonZdrModels` 追加。
- `src/Services/LlmGateway/src/LlmGateway.Api/Routing/EgressMatrix.cs`: `RequiresZeroDataRetention` 追加。
- `src/Services/LlmGateway/src/LlmGateway.Api/Routing/LlmRouter.cs`: ZDR 非対応モデルの除外・フォールバック。
- `src/Services/AiAnalysisService/src/AiAnalysisService.Api/Services/RagOrchestrator.cs`: 既定フォールバック opus。
- テスト: `LlmRouterTests` / `CompletionRoutingEndpointTests` / `TestWebApplicationFactory`（LlmGateway）。
- ドキュメント: 本仕様書 / `IADR-0022` / `docs/tech/tech-requirements.md` / `docs/api/openapi.yaml`（必要時）。

## 非対象（スコープ外）

- Copilot エンドポイントの有効化（契約ティア確定＝08_data-egress-policy のセキュリティレビュー後）。
- 埋め込み経路（FR-03 / ADR-0013）の変更。
- 越境マトリクス値集合の再確定（08_data-egress-policy 確定時に別途追従）。

## 検証結果

- ローカル環境では `dotnet` の実行がサンドボックス制約でブロックされたため、build/test/lint は CI（`/verify`）で確認する。
