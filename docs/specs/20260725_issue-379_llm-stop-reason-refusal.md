---
title: LlmGateway が stop_reason（refusal / max_tokens）を判別し、空応答と拒否を混同しないようにする
type: spec
status: done
related_ids:
  - FR-04
  - FR-11
  - ADR-0010
  - ADR-0025
  - IADR-0022
  - IADR-0037
  - IADR-0101
  - IADR-0104
author: claude
created: 2026-07-25
updated: 2026-07-25
related_specs:
  - "../adr/IADR-0104_llm-stop-reason-refusal.md"
  - "../adr/IADR-0101_default-model-opus-5.md"
  - "../adr/IADR-0022_default-opus-and-fable5-copilot-routes.md"
  - "../adr/IADR-0037_llm-sse-streaming.md"
  - "./20260724_adr-0025_default-model-opus-5.md"
  - "../functional/FR-11_llm-egress-routing.md"
  - "../tests/FR-11_llm-egress-routing.md"
  - "../api/openapi.yaml"
---

# 仕様書: `stop_reason: "refusal"` の判別（issue #379）

## 起点となる計画書（トレーサビリティ）

- 起点 issue: [#379](https://github.com/endazon/microservices-platform/issues/379)
  （`fix(ADR-0025,IADR-0101)`・label `bug` / `priority:should`）。[[IADR-0101]] §フォローアップ 3 の消化。
- 計画根拠: [ADR-0025](../../planning/projects/microservices-platform/07_adr/ADR-0025_llm-model-opus-5.md)
  （グローバル既定を **Claude Opus 5** へ改定・**Accepted**）§結果。
  > Opus 5 は**サイバーセキュリティ領域の安全性分類器**を持ち、`stop_reason: "refusal"`（HTTP 200）を返し得る。
  > 呼び出し側は `content` を読む前に `stop_reason` を確認する必要がある。
- 要求: FR-11（LLM 送信可否の統制・用途別ルーティング）、FR-04（AI 回答と出典）。
- 設計: [ADR-0010](../../planning/projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md)（LLM ゲートウェイ）、
  [[IADR-0022]]（ゲートウェイ経路）、[[IADR-0037]]（SSE ストリーミング）。
- 本作業の実装判断は [[IADR-0104]]。

## 背景と問題（原因の確定）

`ClaudeProvider` は Anthropic 応答から本文だけを取り出し、`stop_reason` を**一切見ていない**。

```csharp
// src/platform/backend/Services/LlmGateway/src/LlmGateway.Api/Composable/Adapters/ClaudeProvider.cs:35
var text = msg.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? "";
return new CompletionResult(text, msg.Usage.InputTokens, msg.Usage.OutputTokens);
```

ストリーミング側（`StreamAsync`）も同様で、`res.Delta?.StopReason` を読まず、最終チャンクは
トークン数のみを伴う。

このため `stop_reason: "refusal"` は **HTTP 200・例外なし・`TextContent` 無し**で到着し、
`text = ""` へ静かに縮退する。エンドポイント（`CompletionEndpoints`）は `Sent: true` のまま
`Text: ""` を返すため、応答契約にもログにも「拒否された」痕跡が残らない。

| 呼び出し側 | 現状の縮退 | 問題 |
| --- | --- | --- |
| `RagOrchestrator`（本リポ） | 空回答／中立フォールバック | 「拒否」と「送信したが空応答」を区別できない |
| AST 取引判断（`trade-decision`・別リポ） | 空応答 → `HoldFallback` | 安全側だが**全判断が Hold に固定**され原因追跡が困難 |
| AST 報告書生成（`report-narrative`・別リポ） | 空文／プレースホルダ散文 | 拒否が運用上見えない |

同じ「空文字へ縮退」は `stop_reason: "max_tokens"`（thinking が上限を食い切った場合。[[IADR-0101]]）
でも起きるため、**両者を区別できないことが運用時の切り分けを難しくしている**。

### 部分本文つき拒否という追加のリスク

安全性分類器は本文の一部を出したあとに停止し得る。この場合 `Text` が**非空のまま `refusal`** になる。
現状の実装はそれを正常応答として返すため、AST 取引判断は `IsNullOrWhiteSpace(dto.Text)` を通過し、
**拒否された断片を根拠に売買判断が行われる**。これは fail-safe 既定（`AST/IADR-0017` の安全側）に反する。

## 受け入れ基準

1. `stop_reason: "refusal"` の受信が、空応答と**区別できる形で**ログに記録される（警告レベル・理由つき）。
2. `stop_reason: "max_tokens"` も同様に区別してログに記録される。
3. `/complete` の応答が終了理由を伝える（`CompletionApiResponse.stopReason`）。既存フィールドの意味は不変。
4. `/complete/stream` の最終イベント（`done`）が終了理由を伝える（`CompletionStreamEvent.stopReason`）。
5. **拒否時は本文を返さない**（部分本文を破棄し `Text` を空にする）。既存の未改修の呼び出し側
   （AST 取引判断・報告書生成）が改修なしで安全側（Hold／プレースホルダ）へ倒れる。
6. `Sent` の意味は変えない。拒否は**外部送信が成立した**うえでの応答であり `Sent=true` のままとする
   （越境監査・課金の意味を壊さない）。
7. 本リポの呼び出し側が拒否を判別する。`RagOrchestrator` は空回答ではなく拒否である旨を返し、
   `LlmGatewayDiagramCoder` は保持理由を `not-codeable` ではなく `llm-refused` として記録する。
8. 正常応答（`end_turn`）は本文・トークン数とも従来どおりで、既存テストが不変で通る。
9. 上記をテストへ写像し、`docs/tests/FR-11_llm-egress-routing.md`（T-16〜T-18）に追記した。
10. `docs/api/openapi.yaml` と通信仕様書を新フィールドに追従させた。
11. `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が platform / knowledge の両ユニットで通る。

## 対応方針（変更範囲）

### 1. ポート（契約の内側）— `Foundation/Ports/ILlmProvider.cs`

- `CompletionStopReasons` 定数（`end_turn` / `max_tokens` / `refusal` / `stop_sequence` / `tool_use`）と
  `IsRefusal(string?)` ヘルパを追加する。比較は大小文字非依存。
- `CompletionResult` / `CompletionChunk` に **既定値つき** `string? StopReason = null` を追加する
  （位置引数レコードの末尾追加＝既存の 3 引数・4 引数呼び出しは不変。`SelfHostedProvider` /
  `CopilotProvider` / テストスタブは無改修で通る）。

### 2. アダプタ — `Composable/Adapters/ClaudeProvider.cs`

- 非ストリーミング: `msg.StopReason` を読む。`refusal` なら**本文を破棄**して空文字を返し、
  `StopReason` に理由を載せる。それ以外は本文をそのまま返し `StopReason` を透過する。
- ストリーミング: `res.Delta?.StopReason`（`message_delta`）を保持し、最終チャンクに載せる。
  デルタは送出済みのため撤回できないが、`done` イベントの `stopReason` で呼び出し側が破棄判断できる
  （[[IADR-0104]] にトレードオフを記録）。

### 3. エンドポイント（縮退ポリシーとログ）— `Foundation/Endpoints/CompletionEndpoints.cs`

- `refusal` は `LogWarning`（モデルが拒否した旨・エンドポイント・モデル）、`max_tokens` も `LogWarning`
  （上限到達・本文が切れ得る旨）で記録する。正常系は従来どおり無出力。
- `CompletionApiResponse` / `CompletionStreamEvent` に `StopReason` を載せる。`Sent` は変更しない。

### 4. 共有契約 — `Shared/Platform.Shared.Contracts/Dtos/CompletionDto.cs`

- `CompletionApiResponse` / `CompletionStreamEvent` の**末尾**に `string? StopReason = null` を追加する。
  既定値つきの追加であり、JSON 上も欠落を許容するため**破壊的変更ではない**（AST 側の部分レコードも無改修で動く）。

### 5. 呼び出し側（本リポの HTTP 経路利用者）

- `RagOrchestrator`（AiAnalysisService・FR-04）
  - 非ストリーミング経路: `refusal` なら本文の代わりに拒否である旨を返す（出典は従来どおり付ける）。
  - ストリーミング経路: `done` の `stopReason` が `refusal` なら、末尾へ拒否である旨のトークンを 1 つ流す。
    部分本文が既に流れている場合は空行（`\n\n`）で区切る。フロント（`SearchChatPage`）は token を
    1 つの文字列へ連結し `white-space: pre-wrap` で表示するため、区切らないと注記が地の文へ溶け込む。
- `LlmGatewayDiagramCoder`（ConversionService・FR-12）
  - 拒否時も「画像として保持」へ収束させる点（deny-by-default）は不変だが、保持理由を
    `not-codeable`（コード化不能）ではなく **`llm-refused`** として記録する。本文が空になる結果
    フェンスが見つからないため、区別しないと「図をコード化できなかった」と誤って記録される。

### 6. ドキュメント

- `docs/adr/IADR-0104_llm-stop-reason-refusal.md`（新規）・`docs/adr/README.md`。
- `docs/api/openapi.yaml`（通信仕様書。`CompletionApiResponse.stopReason`・`/complete` の説明）。
- `docs/functional/FR-11_llm-egress-routing.md`、`docs/tests/FR-11_llm-egress-routing.md`（T-16〜T-18）。

## テスト（TDD・先に失敗させる）

| # | ケース | 期待 |
| --- | --- | --- |
| 1 | Anthropic が `stop_reason: "refusal"`・本文なしを返す | `StopReason="refusal"`・`Text=""` |
| 2 | Anthropic が `stop_reason: "refusal"`・**部分本文あり**を返す | `StopReason="refusal"`・`Text=""`（本文破棄） |
| 3 | Anthropic が `stop_reason: "max_tokens"`・本文が空 | `StopReason="max_tokens"`・`Text=""`（拒否と区別） |
| 4 | Anthropic が `stop_reason: "end_turn"`・本文あり | `StopReason="end_turn"`・本文とトークン数が従来どおり |
| 5 | ストリーミングで `message_delta` が `refusal` を伝える | 最終チャンクの `StopReason="refusal"` |
| 6 | `/complete` が拒否応答を返す | `stopReason:"refusal"`・`sent:true`・`text:""` |
| 7 | `/complete/stream` の `done` | `stopReason` が載る |
| 8 | `RagOrchestrator` が拒否を受ける | 空回答ではなく拒否である旨の本文 |
| 9 | `LlmGatewayDiagramCoder` が拒否を受ける | 画像保持（`Coded=false`）は不変で、理由が `llm-refused` |

`ClaudeProvider` は `AnthropicClient(APIAuthentication, HttpClient)` に**スタブ `HttpMessageHandler`** を
渡して Anthropic 応答 JSON / SSE を固定し、外部送信なしで検証する。

## リスクと自己チェック

- **`Sent=false` にしない**こと。拒否は egress が成立した事象であり、`Sent` を落とすと越境監査・
  課金集計の意味が壊れる。区別は `stopReason` で行う（受け入れ基準 6）。
- **本文破棄は `refusal` のみ**に限る。`max_tokens` の部分本文は正当な途中結果であり破棄しない
  （破棄すると [[IADR-0101]] が想定した「切れた本文」の観測ができなくなる）。
- **契約の追加は末尾・既定値つき**に限る。位置引数レコードの途中挿入は AST を含む呼び出し側を壊す。
- ストリーミングは送出済みデルタを撤回できない。`done` の `stopReason` を見て破棄するのは呼び出し側の責務。

## 非対象・除外

- 拒否された要求のリトライ戦略・プロンプト改変（issue #379 §非スコープ）。
- モデル変更・`thinking` パラメータの送信（[[IADR-0101]] で不採用）。
- AST 側（別リポジトリ・submodule）の実装変更。本 PR では**契約追加のみ**行い、AST は無改修で
  安全側へ倒れる（受け入れ基準 5）。AST が `stopReason` を活用する改修は AST 側 issue で扱う。
- `SelfHostedProvider` / `CopilotProvider` の `stop_reason` 対応（OpenAI 互換 `finish_reason` は
  別語彙であり、必要になった時点で別途対応）。

## 検証

- `dotnet build src/platform/backend/backend.slnx` / `dotnet test src/platform/backend/backend.slnx`
- `dotnet build src/knowledge/backend/backend.slnx` / `dotnet test src/knowledge/backend/backend.slnx`
- `dotnet format <slnx> --verify-no-changes`（両ユニット）
