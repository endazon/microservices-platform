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
  - IADR-0110
  - IADR-0400
  - NFR-17
  - NFR-19
  - NFR-28
author: claude
created: 2026-07-25
updated: 2026-10-08
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0025_llm-model-opus-5.md (グローバル既定を Opus 5 へ改定・Accepted。§結果が stop_reason 確認を要求)
  - planning:projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md (LLM ゲートウェイ設計・Accepted・本文凍結)
---

# IADR-0104: `stop_reason` の判別と拒否の伝達

- 状態: Accepted
- 日付: 2026-07-25
- 決定者: claude（実装）

## 起点・関連

- 起点 issue: [#379](https://github.com/endazon/microservices-platform/issues/379)（`bug` / `priority:should`）。
  [IADR-0101](./IADR-0101_default-model-opus-5.md) §フォローアップ 3「`stop_reason: "refusal"` のハンドリング検討」の消化。
- 計画根拠: ADR-0025（計画リポ）
  §結果「Opus 5 は**サイバーセキュリティ領域の安全性分類器**を持ち、`stop_reason: "refusal"`（HTTP 200）
  を返し得る。呼び出し側は `content` を読む前に `stop_reason` を確認する必要がある」。
- 仕様書: `docs/specs/20260725_issue-379_llm-stop-reason-refusal.md`。

## コンテキストと課題

`ClaudeProvider` は Anthropic 応答から `TextContent` だけを取り出し、`stop_reason` を読んでいなかった。
`refusal` は **HTTP 200・例外なし・本文なし**で到着するため、`Text=""` へ静かに縮退し、
`CompletionEndpoints` は `Sent: true` のまま空文字を返していた。結果として

- 監査ログ上「拒否」と「送信したが空応答」が区別できない。
- `stop_reason: "max_tokens"`（thinking が上限を食い切る。[IADR-0101](./IADR-0101_default-model-opus-5.md)）とも区別できない。
- 部分本文を出した直後に拒否された場合、**拒否された断片が正常応答として下流に流れる**。

[IADR-0101](./IADR-0101_default-model-opus-5.md) のマージで既定層（`PurposeModels.default` / `DefaultModel`）が `claude-opus-5` に
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
  [IADR-0101](./IADR-0101_default-model-opus-5.md) は「思考が上限を食い本文が途中で切れる」ことを既知の劣化として記録しており、
  その断片を破棄すると症状が見えなくなる。拒否は逆に、断片が下流の判断材料になってはならない。
- **末尾・既定値つきの追加**に限れば位置引数レコードの既存呼び出しは不変で、JSON も欠落を許容するため
  破壊的変更にならない。AST 側の部分レコード（`CompletionResponse(string? Text, bool Sent, ...)`）も無改修で動く。

## 結果

- 良い影響: 拒否・上限到達・正常終了が**ログと応答契約の両方で区別**できる。未改修の呼び出し側も
  改修済みの呼び出し側も、それぞれ安全側／説明可能な側へ倒れる。ADR-0025 §結果の
  「`content` を読む前に `stop_reason` を確認する」要求を満たす。
- 悪い影響 / トレードオフ:
  - **ストリーミングは送出済みデルタを撤回できない。** `refusal` は末尾の `message_delta` で確定するため、
    それ以前のデルタは届いてしまう。`done` の `stopReason` を見て破棄・注記するのは**呼び出し側の責務**
    とする（`RagOrchestrator` は末尾へ拒否である旨を空行区切りで追記する）。ゲートウェイで全デルタを
    バッファすれば防げるが、[IADR-0037](./IADR-0037_llm-sse-streaming.md) の逐次表示の価値を失うため採らない。
  - `refusal` 時は**拒否直前の部分本文がどこにも残らない**（ログにも出さない。分類器が止めた内容を
    監査ログへ写さないため）。残るのは「拒否された」事実のみ。
  - 契約にフィールドが 1 つ増える（`openapi.yaml`・通信仕様書の追従が必要）。
- フォローアップ:
  1. **AST 側（別リポジトリ）で `stopReason` を活用する**。現状は `Text` が空になることで安全側へ倒れるが、
     `HoldFallback` の理由や報告書のプレースホルダに「モデルが拒否」を明示できると原因追跡が速い。
  2. **`SelfHostedProvider` / `CopilotProvider` の終了理由**。OpenAI 互換 API は `finish_reason`
     （`length` / `content_filter` 等）という別語彙を持つ。既定経路が無効の間は未対応とし、
     有効化時に写像を決める。
  3. **拒否率の可観測性**。`stopReason` 別カウンタ（メトリクス）で既定層の劣化を検知できる。現状はログのみ。

## 関連

- Supersedes: なし（[IADR-0101](./IADR-0101_default-model-opus-5.md) §フォローアップ 3 を消化する）
- Superseded by: なし
- 関連要求 / UC: FR-11（LLM 送信可否の統制）、FR-04（AI 回答と出典）
- 関連 IADR: [IADR-0022](./IADR-0022_default-opus-and-fable5-copilot-routes.md)（ゲートウェイ経路）、[IADR-0037](./IADR-0037_llm-sse-streaming.md)（SSE ストリーミング）、[IADR-0101](./IADR-0101_default-model-opus-5.md)（既定 Opus 5）

> ［2026-10-08 追記 / #1819］**`Sent=false` の原因の種類を応答契約に載せ、越境拒否に運用ログを足した**（本 ADR の「`Sent` と `StopReason` は独立した軸」を、未送信の側へ延ばす決定）。
> 起点は PoC の事故である: AST の取引判断が 132 件連続で `Sent=false` を受けたが、原因（越境拒否か・プロバイダ未登録か・上流の不調か）を事後に確定できなかった。
> 応答は原因を `Text` の文言でしか区別しておらず、越境拒否の枝はログを出さず（計器だけ）、Prometheus の無い経路では計器も読めなかった。
> 新しい IADR は起こさない（採番が並行の PR と競合していたため。決定は応答契約の末尾追加という本 ADR と同じ型である）。
>
> 1. **`CompletionApiResponse` / `CompletionStreamEvent` の末尾に `string? FailureKind = null` と `int? UpstreamStatusCode = null` を足す**（決定 1 と同じ末尾・既定値つき）。
>    値は共有契約 `CompletionFailureKinds` の `egress_denied` / `provider_missing` / `upstream_error`。`UpstreamStatusCode` は `upstream_error` で上流が HTTP 状態を返したときだけ載る（輸送の失敗は null）。
>    `Sent=true` では両方 null。**`Sent` / `Text` / `RoutingReason` / `StopReason` の意味は変えない。**
>    gRPC（`completion.proto`）は `CompleteResponse` の 9・10、`CompletionStreamEvent` の 10・11 に同じ項目を足す（空文字 / 0 が「無い」）。
> 2. **語彙は計器の `llm.result`（[IADR-0110](./IADR-0110_llm-completion-stop-reason-metrics.md) 決定 2）と同じ文字列**にし、計器の定数は契約の定数を引く（2 か所に書かない）。IADR-0110 の値域は変えていない。
> 3. **enum にしない**（本 ADR §決定の末段と同じ理由。語彙が増えたとき古い呼び出し側が既定値へ黙って落ちない）。
> 4. **越境拒否の枝に warn を足す**（理由の文言・用途・機密区分）。(用途, 理由) の組ごとに初回は即時・以後は 5 分ごとに抑えた件数つきの 1 行（`LogOccurrenceThrottle`）。
>    抑制の鍵は値域を閉じた用途（計器と同じ正規化）で作り、ログへ出す用途は制御文字を落とす（NFR-28。ルータの `Sanitize` を共有）。
>    ルータの `LLM routing denied`（ADR-0010 の監査ログ・呼び出しごと）は**抑制しない**（監査の件数を欠けさせない）。
>
> 退けた案: ①`Text` の文言を規約化して呼び出し側に解釈させる（文言は表示用で、変えると黙って割れる）。②`Sent` を列挙型へ広げる（`Sent` は越境監査の軸であり、意味を変えると本 ADR 決定 4 の前提が崩れる）。
> ③要約をタイマーで自発的に出す（状態とスレッドを持つ割に、発生が止まった後の件数は運用上の価値が小さい。残余として記録）。
> 回帰は `SentFalseObservabilityTests`（6 経路・抑制・無害化・写像の往復）と `GrpcCompleteTests` / `GrpcCompleteStreamTests` の追加表明で固定した（変異 3 種で赤を確認）。
