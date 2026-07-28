---
title: SelfHosted / Copilot プロバイダの finish_reason を StopReason へ写像する（issue #394）
type: spec
status: done
related_ids:
  - FR-11
  - FR-04
  - FR-12
  - UC-01
  - UC-02
  - ADR-0010
  - ADR-0025
  - IADR-0022
  - IADR-0037
  - IADR-0101
  - IADR-0104
  - IADR-0109
author: claude
created: 2026-07-28
updated: 2026-07-28
related_specs:
  - "../adr/IADR-0109_openai-finish-reason-normalization.md"
  - "../adr/IADR-0104_llm-stop-reason-refusal.md"
  - "../adr/IADR-0022_default-opus-and-fable5-copilot-routes.md"
  - "./20260725_issue-379_llm-stop-reason-refusal.md"
  - "../functional/FR-11_llm-egress-routing.md"
  - "../tests/FR-11_llm-egress-routing.md"
  - "../api/openapi.yaml"
---

# 仕様書: OpenAI 互換 `finish_reason` の写像（issue #394）

## 起点となる計画書（トレーサビリティ）

- 起点 issue: [#394](https://github.com/endazon/microservices-platform/issues/394)（`enhancement`）。
  [[IADR-0104]] §フォローアップ 2「`SelfHostedProvider` / `CopilotProvider` の終了理由」の消化
  （起点は #379 / PR #391）。
- 要求: **FR-11**（LLM 送信可否の統制・用途別ルーティング）。呼び出し側は FR-04（`RagOrchestrator`）・
  FR-12（`LlmGatewayDiagramCoder`）・AST `trade-decision`。UC-01 / UC-02。
- 設計: [ADR-0010](../../planning/projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md)（LLM ゲートウェイ・本文凍結）、
  [ADR-0025](../../planning/projects/microservices-platform/07_adr/ADR-0025_llm-model-opus-5.md)（`stop_reason` 確認の要求）、
  [[IADR-0022]]（セルフホスト＝ティアA／Copilot＝ティアC の経路）、[[IADR-0104]]（`stopReason` の契約と refusal の本文破棄）、
  [[IADR-0037]]（SSE ストリーミング）。
- 本作業の実装判断は [[IADR-0109]]。

## 背景と問題（原因の確定）

[[IADR-0104]]（PR #391）は共有契約とポートへ `StopReason` を追加したが、**写像を実装したのは
`ClaudeProvider` だけ**である。

`SelfHostedProvider` / `CopilotProvider` はいずれも OpenAI 互換 `/chat/completions` を呼び、応答を

```csharp
private sealed record OpenAiCompletionResponse(List<OpenAiChoice>? Choices, OpenAiUsage? Usage);
private sealed record OpenAiChoice(OpenAiMessage? Message);   // ← finish_reason を持たない
```

で受けている。`choices[].finish_reason` を読んでいないため、`CompletionResult.StopReason` は
**既定値 `null` のまま**返る。結果として、ティアA（セルフホスト）／ティアC（Copilot）経路では
#379 が解こうとした混同（拒否・上限到達・正常終了の区別不能）が**そのまま残る**。

呼び出し側は既に `stopReason` を見る実装（`RagOrchestrator` の拒否注記・`LlmGatewayDiagramCoder` の
`llm-refused`）へ移行済みであり、**同じ契約がプロバイダによって意味を持ったり持たなかったりする**。
とくに `content_filter`（OpenAI の安全性フィルタ停止）は、[[IADR-0104]] が `refusal` について定めた
**本文破棄の fail-safe が効かない**まま断片が下流へ流れる。

### 現状の到達可能性

両エンドポイントは既定 `Enabled=false`（[[IADR-0022]]）だが、`values` / `appsettings` の設定 1 つで
有効化できる。有効化した時点で上記の欠落が表面化するため、有効化前に塞ぐのが安い。

### 語彙の差（OpenAI 互換 ↔ 契約の正準語彙）

| OpenAI `finish_reason` | 意味 | 契約の正準語彙（`CompletionStopReasons`） |
| --- | --- | --- |
| `stop` | 正常終了（停止トークン到達を含む） | `end_turn` |
| `length` | `max_tokens` 到達で打ち切り | `max_tokens` |
| `content_filter` | コンテンツフィルタによる停止 | `refusal` |
| `tool_calls` | ツール呼び出しで停止 | `tool_use` |
| `function_call` | 旧 function calling（OpenAI で非推奨） | `tool_use` |
| 上記以外・将来の追加値 | 不明 | **原文のまま透過**（warn ログ） |

## 対象範囲

### 変更する

| 対象 | 変更内容 |
| --- | --- |
| `OpenAiFinishReasons.cs`（新規） | `finish_reason` → 正準語彙の写像。未知値は原文透過＋`Unmapped=true` を返す |
| `SelfHostedProvider.cs` | `choices[].finish_reason` を読み、写像して `CompletionResult.StopReason` へ。`refusal` 相当は本文破棄。未知値は warn ログ |
| `CopilotProvider.cs` | 同上（同じヘルパを共用） |
| `OpenAiFinishReasonTests.cs`（新規） | 写像表そのもの（正準語彙・未知値・大小文字・null）の単体テスト |
| `OpenAiProviderStopReasonTests.cs`（新規） | 両プロバイダの代表値を HTTP スタブで固定（T-20） |
| `docs/adr/IADR-0109_*`（新規）・`docs/adr/README.md` | 決定の記録と索引 |
| `docs/functional/FR-11` | プロバイダ横断の `stopReason` 節を追加 |
| `docs/tests/FR-11` | T-20 を追加 |
| `docs/api/openapi.yaml` | `stopReason` の記述を「プロバイダ横断で正準語彙へ正規化」に是正 |

### 変更しない（意図的に対象外）

- **共有契約（`CompletionApiResponse` / `CompletionStreamEvent` / `CompletionResult` / `CompletionChunk`）**。
  フィールドは既に存在し、型も変えない。OpenAI 固有の語彙を共有契約へ持ち込まない。
- **`CompletionStopReasons`（共有契約の正準語彙）**。OpenAI の語彙定数は追加せず、写像は
  ゲートウェイ側（トランスポートの関心事）に閉じる。
- **`ClaudeProvider`**（[[IADR-0104]] で対応済み。無変更）。
- **エンドポイントの有効化**（`Enabled=false` の既定・ティア割当・`Llm:Copilot:*` の設定値）。
  本作業は写像のみで、越境ポリシーには触れない。
- **`StreamAsync` の個別実装**。両プロバイダは `ILlmProvider` の既定実装（`CompleteAsync` を単一チャンクへ
  縮退）を使うため、`StopReason` は最終チャンクへ**自動的に載る**（[[IADR-0037]]）。個別実装は追加しない。
- #395（拒否率の可観測化）・#403（縮退応答のモデル名）。別 issue・別 PR。

## 決定（要約。詳細は [[IADR-0109]]）

**OpenAI 互換 `finish_reason` はプロバイダ境界で契約の正準語彙（`CompletionStopReasons`）へ正規化する。
未知値は既定値へ潰さず原文のまま透過し、warn ログに残す。`content_filter` → `refusal` は
[[IADR-0104]] の決定に従い本文も破棄する。**

## 実装方針（TDD）

1. **Red**: `OpenAiFinishReasonTests`（写像表）と `OpenAiProviderStopReasonTests`（両プロバイダの
   代表値・本文破棄・未知値の透過とログ）を先に追加する。現状は `StopReason` が常に `null` のため失敗する。
2. **Green**: `OpenAiFinishReasons` を追加し、両プロバイダで `finish_reason` を読んで写像する。
3. **追随**: 機能仕様書・テスト仕様書・OpenAPI・IADR・索引。
4. **検証**: `dotnet test` / `dotnet format --verify-no-changes`（platform・knowledge 両ユニット）。

## テスト観点

| ID | 観点 | 期待 |
| --- | --- | --- |
| T-20a | 写像表（`OpenAiFinishReasons`） | `stop`→`end_turn` / `length`→`max_tokens` / `content_filter`→`refusal` / `tool_calls`・`function_call`→`tool_use`。大小文字非依存 |
| T-20b | 未知値 | 原文のまま透過し `Unmapped=true`（既定値へ潰さない） |
| T-20c | 欠落（`finish_reason` なし・null） | `StopReason` は `null`。warn ログを出さない（未知語彙ではないため） |
| T-20d | `SelfHostedProvider` の代表値 | `stop`/`length`/`content_filter`/未知 が `CompletionResult.StopReason` へ載る |
| T-20e | `CopilotProvider` の代表値 | 同上（同じ写像） |
| T-20f | `content_filter` の本文破棄 | 本文が空になる（[[IADR-0104]] の refusal と一貫） |
| T-20g | `length` の本文保持 | 途中結果を破棄しない（[[IADR-0104]] と一貫） |
| T-20h | 未知値の warn ログ | プロバイダが warn を 1 件記録する |
| T-20i | ストリーミング（既定実装） | 最終チャンク（`Done=true`）に写像後の `StopReason` が載る |

## 受け入れ基準（issue #394 §受け入れ基準に対応）

- [x] `SelfHostedProvider` / `CopilotProvider` が `finish_reason` を読み、`CompletionResult.StopReason` へ写像する
- [x] `length` / `content_filter` / `stop` の正規化方針が決まり、実装 ADR に記録されている
- [x] 未知の `finish_reason` は既定値へ潰さず透過し、ログに残る
- [x] `content_filter`（拒否相当）時の本文の扱いが #391 の決定と一貫している
- [x] 上記をテストへ写像し、`docs/tests/FR-11_llm-egress-routing.md` に追記した
- [x] 通信仕様書（`docs/api/openapi.yaml`）の `stopReason` 記述がプロバイダ横断で正しい

## 完了条件（DoD）

- `dotnet build` / `dotnet test` が platform・knowledge 両ユニットで通る
- `dotnet format --verify-no-changes` が両ユニットで通る
- 上表の受け入れ基準がすべてチェック済み
- `docs/DEFINITION_OF_DONE.md` を満たす
