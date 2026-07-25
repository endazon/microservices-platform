---
title: IADR-0104 stop_reason を応答契約に載せ、refusal は本文を破棄して「空応答」と区別する
type: impl-adr
status: Accepted
related_ids:
  - FR-04
  - FR-11
  - ADR-0010
  - ADR-0025
  - IADR-0022
  - IADR-0037
  - IADR-0101
author: claude
created: 2026-07-25
updated: 2026-07-25
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0025_llm-model-opus-5.md (グローバル既定を Opus 5 へ改定・Accepted。§結果が stop_reason 確認を要求)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md (LLM ゲートウェイ設計・Accepted・本文凍結)"
---

# IADR-0104: `stop_reason` の判別と拒否の伝達

- 状態: Accepted
- 日付: 2026-07-25
- 決定者: claude（実装）

## 起点・関連

- 起点 issue: [#379](https://github.com/endazon/microservices-platform/issues/379)（`bug` / `priority:should`）。
  [[IADR-0101]] §フォローアップ 3「`stop_reason: "refusal"` のハンドリング検討」の消化。
- 計画根拠: [ADR-0025](../../planning/projects/microservices-platform/07_adr/ADR-0025_llm-model-opus-5.md)
  §結果「Opus 5 は**サイバーセキュリティ領域の安全性分類器**を持ち、`stop_reason: "refusal"`（HTTP 200）
  を返し得る。呼び出し側は `content` を読む前に `stop_reason` を確認する必要がある」。
- 仕様書: `docs/specs/20260725_issue-379_llm-stop-reason-refusal.md`。

## コンテキストと課題

`ClaudeProvider` は Anthropic 応答から `TextContent` だけを取り出し、`stop_reason` を読んでいなかった。
`refusal` は **HTTP 200・例外なし・本文なし**で到着するため、`Text=""` へ静かに縮退し、
`CompletionEndpoints` は `Sent: true` のまま空文字を返していた。結果として

- 監査ログ上「拒否」と「送信したが空応答」が区別できない。
- `stop_reason: "max_tokens"`（thinking が上限を食い切る。[[IADR-0101]]）とも区別できない。
- 部分本文を出した直後に拒否された場合、**拒否された断片が正常応答として下流に流れる**。

[[IADR-0101]] のマージで既定層（`PurposeModels.default` / `DefaultModel`）が `claude-opus-5` に
なったため、この経路は現構成で実際に起き得る。

## 検討した選択肢

1. **応答契約に `stopReason` を追加し、`refusal` のみ本文を破棄する（採用）**
   — 既存フィールドの意味を変えず、末尾に既定値つきフィールドを足すだけで済む。未改修の呼び出し側
   （AST 取引判断・報告書生成。別リポジトリのため本 PR では改修できない）は `Text` が空になることで
   従来どおり安全側（Hold／プレースホルダ散文）へ倒れ、改修済みの呼び出し側は理由を判別できる。
2. `refusal` を例外にする — `CompletionEndpoints` の `catch` が「呼び出し先が現在利用できません」へ
   倒すため、**呼び出し先障害と拒否が混ざる**。issue #379 が解こうとしている混同を別の形で再生産する。
3. `refusal` を `Sent=false` にする — 未改修の呼び出し側もそのまま安全側へ倒れる点は魅力的だが、
   `Sent` は FR-11 の**越境（egress）が成立したか**を表す監査上の意味を持つ。拒否は
   「外部へ送信し、モデルが応答した」事象であり、`Sent=false` にすると越境監査・課金集計が壊れる。
4. 本文を破棄せず `stopReason` だけ足す — 契約は最小だが、未改修の AST 取引判断が
   `IsNullOrWhiteSpace(dto.Text)` を通過し、**拒否された断片を根拠に売買判断が行われる**。fail-safe に反する。

## 決定

1. `CompletionApiResponse` / `CompletionStreamEvent`（共有契約）と `CompletionResult` / `CompletionChunk`
   （ポート）の**末尾に既定値つき `string? StopReason = null`** を追加する。
2. `ClaudeProvider` は非ストリーミングで `msg.StopReason` を、ストリーミングで `res.Delta?.StopReason`
   （`message_delta`）を読み、そのまま透過する。
3. **`refusal` のときだけ本文を破棄**し `Text` を空にする。`max_tokens` の部分本文は破棄しない。
4. `CompletionEndpoints` は `refusal` / `max_tokens` を **`LogWarning` で区別して記録**する。
   `Sent` は変更しない（拒否も `Sent=true`）。
5. 本リポジトリの呼び出し側は `refusal` を判別する。`RagOrchestrator`（FR-04）は空回答ではなく
   「AI が回答を拒否した」旨を出典つきで返し、`LlmGatewayDiagramCoder`（FR-12）は画像保持の理由を
   `not-codeable` ではなく `llm-refused` として記録する（いずれも縮退先そのものは変えない）。

`refusal` の判定は大小文字非依存の文字列比較（`CompletionStopReasons.IsRefusal`）で行う。
DTO 側を `enum` にしないのは、Anthropic が将来 `stop_reason` の語彙を増やしたときに
**未知の値が既定値へ黙って落ちる**のを避けるためである（未知値はそのまま文字列で透過し、ログに残る）。

## 理由

- **`Sent` の意味を守る**ことが FR-11 の越境統制の前提である（選択肢 3 を退けた理由）。
  「送ったか」と「モデルが答えたか」は独立した軸であり、別フィールドで表す。
- **本文破棄を `refusal` に限る**のは、`max_tokens` の途中結果が正当な観測対象だからである。
  [[IADR-0101]] は「思考が上限を食い本文が途中で切れる」ことを既知の劣化として記録しており、
  その断片を破棄すると症状が見えなくなる。拒否は逆に、断片が下流の判断材料になってはならない。
- **末尾・既定値つきの追加**に限れば位置引数レコードの既存呼び出しは不変で、JSON も欠落を許容するため
  破壊的変更にならない。AST 側の部分レコード（`CompletionResponse(string? Text, bool Sent, ...)`）も無改修で動く。

## 結果

- 良い影響: 拒否・上限到達・正常終了が**ログと応答契約の両方で区別**できる。未改修の呼び出し側も
  改修済みの呼び出し側も、それぞれ安全側／説明可能な側へ倒れる。ADR-0025 §結果の
  「`content` を読む前に `stop_reason` を確認する」要求を満たす。
- 悪い影響 / トレードオフ:
  - **ストリーミングは送出済みデルタを撤回できない。** `refusal` は `message_delta`（末尾）で確定するため、
    それ以前に流れたデルタは呼び出し側に届いてしまう。`done` イベントの `stopReason` を見て
    表示を破棄するのは**呼び出し側の責務**である（`RagOrchestrator` は拒否である旨を追記する実装とした）。
    完全に防ぐにはゲートウェイ側で全デルタをバッファする必要があり、[[IADR-0037]] が採用した
    真のストリーミング（逐次表示）の価値を失うため採らない。
  - `refusal` 時に本文を破棄するため、**拒否直前の部分本文はどこにも残らない**（ログにも出さない。
    安全性分類器が止めた内容を監査ログへ写すのは望ましくないため）。残るのは「拒否された」事実のみ。
  - 契約にフィールドが 1 つ増える（`openapi.yaml`・通信仕様書の追従が必要）。
- フォローアップ:
  1. **AST 側（別リポジトリ）で `stopReason` を活用する**。現状は `Text` が空になることで安全側へ倒れるが、
     `HoldFallback` の理由や報告書のプレースホルダに「モデルが拒否」を明示できると原因追跡が速い。
     AST 側 issue で扱う（本リポジトリでは修正不可）。
  2. **`SelfHostedProvider` / `CopilotProvider` の終了理由**。OpenAI 互換 API は `finish_reason`
     （`length` / `content_filter` 等）という別語彙を持つ。現状は既定経路が無効のため未対応とし、
     有効化する際に語彙の写像を決める。
  3. **拒否率の可観測性**。`stopReason` 別のカウンタ（メトリクス）を出すと、既定層の劣化を
     ダッシュボードで検知できる。現状はログのみ。

## 関連

- Supersedes: なし（[[IADR-0101]] §フォローアップ 3 を消化する）
- Superseded by: なし
- 関連要求 / UC: FR-11（LLM 送信可否の統制）、FR-04（AI 回答と出典）
- 関連 IADR: [[IADR-0022]]（ゲートウェイ経路）、[[IADR-0037]]（SSE ストリーミング）、[[IADR-0101]]（既定 Opus 5）
