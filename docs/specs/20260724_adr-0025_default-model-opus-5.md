---
title: LLM 既定モデルを claude-opus-4-8 → claude-opus-5 へ追従（ADR-0025）
type: spec
status: done
related_ids:
  - FR-11
  - ADR-0010
  - ADR-0025
  - IADR-0022
  - IADR-0101
author: claude
created: 2026-07-24
updated: 2026-07-24
related_specs:
  - "../adr/IADR-0101_default-model-opus-5.md"
  - "../adr/IADR-0022_default-opus-and-fable5-copilot-routes.md"
  - "../functional/FR-11_llm-egress-routing.md"
  - "../tests/FR-11_llm-egress-routing.md"
---

# 仕様書: LLM 既定モデルを Claude Opus 5 へ追従（ADR-0025）

## 起点となる計画書（トレーサビリティ）

- 計画根拠: [ADR-0025](../../planning/projects/microservices-platform/07_adr/ADR-0025_llm-model-opus-5.md)
  「LLM 利用モデルの改定 — グローバル既定を Claude Opus 5 へ更新」（Accepted・2026-07-24）。
  グローバル既定（用途指定なし）を `claude-opus-4-8` → `claude-opus-5` に改定する決定。
- 上位のゲートウェイ設計は [ADR-0010](../../planning/projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md)（Accepted・本文凍結）。
  用途別モデル表は `06_technical/04_ai-rag-stack.md`。
- 要求: FR-11（LLM 送信可否の統制・用途別ルーティング）。
- 既存の実装決定: [[IADR-0022]]（既定 Opus・fable-5／Copilot 経路）。本作業はその**モデル版数のみ**を更新する。
- 本作業の実装判断は [[IADR-0101]]（既定 `max_tokens` の引き上げ根拠を含む）。

## 背景と問題

ADR-0025 により計画側のグローバル既定が Opus 5 へ改定された。実装側は `Llm:Model` /
`Llm:Routing:PurposeModels.default` / エンドポイント `DefaultModel`・`Models`、およびコードの
フォールバック値が `claude-opus-4-8` のままであり、計画と実装が乖離している。

加えて **Opus 5 は Opus 4.8 との間に 1 点の実質的な挙動差**がある。

- Opus 4.8: `thinking` パラメータを**省略すると思考なし**で動作する。
- Opus 5: `thinking` を**省略すると adaptive thinking が有効**になる。

`max_tokens` は思考トークンと本文の**合算上限**であるため、モデル ID だけを差し替えると、
現行の `MaxTokens = 1024` では思考が上限を食い、**RAG 回答が途中で切れる**。
本作業ではモデル ID の差し替えと既定 `max_tokens` の引き上げを一体で行う。

現行実装（`ClaudeProvider`）は `thinking` / `temperature` / `top_p` / `top_k` / assistant prefill を
一切送っていないため、Opus 5 で 400 になる破壊的パラメータは**存在しない**（調査済み）。

## 受け入れ基準

1. グローバル既定モデルが `claude-opus-5` になる（`Llm:Model` / `Llm:DefaultModel` / コードのフォールバック）。
2. 用途別ルーティングの `default` が `claude-opus-5` を返す。他用途は不変。
   - `default→claude-opus-5`, `rag-answer→claude-sonnet-4-6`, `diagram-coding→claude-haiku-4-5`,
     `analysis→claude-fable-5`
3. claude エンドポイントの `Models` 許可一覧に `claude-opus-5` が含まれる。
   > **改定（IADR-0102）**: 当初は「`claude-opus-4-8` は含まれない」としていたが、`AST/ADR-0011` 追従で
   > `trade-decision` を `claude-opus-4-8` にピン留めするため**差し戻した**。`PurposeModels` の値は
   > `Models` に含まれないと `ResolveModel` が `DefaultModel` へフォールバックし、ピン留めが無効化される。
   > `Models` は「利用を許可するモデル集合」であり、グローバル既定が何かとは独立の概念である。
4. ZDR 要件区分（confidential/restricted）の `analysis` は ZDR 非対応の `claude-fable-5` を除外し、
   **ZDR 対応の `claude-opus-5`** へフォールバックする（従来 opus-4-8 が担っていた役割を版数だけ引き継ぐ）。
5. 既定 `max_tokens` が思考分の余裕を含む値へ引き上げられ、既定モデルでの回答が途中で切れない。
6. `dotnet build` / `dotnet test` が platform・knowledge の両ユニットで通り、`dotnet format` が差分ゼロ。

## 対応方針（変更範囲）

**設定・コード（platform / knowledge）**

- `src/platform/.../LlmGateway.Api/appsettings.json`: `Llm:Model`、`PurposeModels.default`、
  claude エンドポイントの `DefaultModel`・`Models`。
- `src/platform/.../Composable/Adapters/ClaudeProvider.cs`: フォールバック既定値とヘッダコメント。
- `src/platform/.../Foundation/Routing/LlmRoutingOptions.cs`: 用途別既定の説明コメント。
- `src/platform/backend/Shared/Platform.Shared.Contracts/Dtos/CompletionDto.cs`:
  `CompletionApiRequest.MaxTokens` 既定値。**HTTP 経路（`/complete`・`/complete/stream`）で実際に効く既定はここ**
  （エンドポイントは `req.MaxTokens` を常に明示的にプロバイダへ渡すため）。
- `src/platform/.../Foundation/Ports/ILlmProvider.cs`: `CompletionRequest.MaxTokens` 既定値（内部経路用）。
- `src/knowledge/.../AiAnalysisService.Api/Foundation/Services/RagOrchestrator.cs`:
  `Llm:DefaultModel` フォールバック（3 箇所）と `CompletionApiRequest` の `MaxTokens`（2 箇所）。
- `deploy/docker-compose.yml`: `Llm__Model` と併記コメント。

**テスト**

- `LlmRouterTests` / `CompletionRoutingEndpointTests` / `TestWebApplicationFactory` の
  期待モデル ID を `claude-opus-5` へ更新（ケースの意味は変えない）。

**仕様書**

- `docs/functional/FR-11_llm-egress-routing.md`・`docs/tests/FR-11_llm-egress-routing.md` の
  既定モデル記述を更新。

## リスクと自己チェック

- **思考の既定有効化（最重要）**: `max_tokens` 据え置きは回答途中切れに直結する。引き上げ根拠は [[IADR-0101]]。
- **既定値の引き上げでは救済されない呼び出し元がある**: `max_tokens` を明示指定している呼び出しは既定値の
  影響を受けない。ゲートウェイ利用者を洗い出した結果、`src/ai-stock-trading`（submodule）の 2 箇所
  （`HttpLlmCompletionClient` / `HttpReportNarrativeDrafter`）が `MaxTokens: 1024` をハードコードし、
  いずれも `purpose` 未登録で `default` へ着地する。本リポジトリからは修正できないため、
  ai-stock-trading 側の対応を先行または同時にマージする必要がある（[[IADR-0101]] フォローアップ 5）。
- **レート制限**: Opus 5 は Opus 4.x 系の共通プールとは**別枠**。既定層のトラフィック移行前に枠を確認する（運用フォローアップ）。
- **`stop_reason: "refusal"`**: Opus 5 はサイバー系の安全性分類器を持ち HTTP 200 + `refusal` を返し得る。
  現行 `ClaudeProvider` は本文先頭テキストを取り出すのみで例外にはならない（空応答へ縮退）。
  ハンドリング追加は本作業のスコープ外とし、[[IADR-0101]] にフォローアップとして記録する。
- **ZDR**: Opus 5 に 30 日保持要件は無く、`NonZdrModels` は `claude-fable-5` のみで不変。T-13 の意味は保たれる。

## 非対象・除外

- `rag-answer` / `diagram-coding` / `analysis` の割当モデル変更（ADR-0022 の範囲・本作業では不変更）。
- `thinking` / `effort` / `fallbacks` パラメータの新規送信（計画に無い機能追加のため行わない）。
- マージ済みの point-in-time 記録（`docs/specs/20260706_*`・`feedback/*`・[[IADR-0022]] 本文）の追随改変。
  履歴不変の原則に従い、最新の roster は本仕様書と [[IADR-0101]] を正とする。

## 検証

- `dotnet build src/platform/backend/backend.slnx` / `src/knowledge/backend/backend.slnx`
- `dotnet test`（両ユニット）— 特に `LlmRouterTests` の既定・ZDR フォールバック系
- `dotnet format --verify-no-changes`（両ユニット）
- `grep -rn "claude-opus-4-8" src/ deploy/ docs/functional docs/tests` が 0 件
