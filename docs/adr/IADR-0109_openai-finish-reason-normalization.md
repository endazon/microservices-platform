---
title: IADR-0109 OpenAI 互換 finish_reason はプロバイダ境界で正準語彙へ正規化し、未知値は透過する
type: impl-adr
status: Accepted
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
author: claude
created: 2026-07-28
updated: 2026-07-28
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md (LLM ゲートウェイ設計・Accepted・本文凍結)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0025_llm-model-opus-5.md (グローバル既定 Opus 5・Accepted。§結果が stop_reason 確認を要求)"
---

# IADR-0109: OpenAI 互換 `finish_reason` の正規化

- 状態: Accepted
- 日付: 2026-07-28
- 決定者: claude（実装）

## 起点・関連

- 起点 issue: [#394](https://github.com/endazon/microservices-platform/issues/394)（`enhancement`）。
  [[IADR-0104]] §フォローアップ 2 の消化（起点は #379 / PR #391）。
- 仕様書: `docs/specs/20260728_issue-394_openai-finish-reason.md`。
- 本 IADR は**プロバイダ境界での語彙の写像**のみを扱う。越境ルーティング（[[IADR-0022]]）・
  エンドポイントの有効化・共有契約の形（[[IADR-0104]]）は変更しない。

## コンテキストと課題

[[IADR-0104]] は共有契約とポートへ `StopReason` を追加したが、**写像を実装したのは `ClaudeProvider`
だけ**である。`SelfHostedProvider`（ティアA）／`CopilotProvider`（ティアC）は OpenAI 互換
`/chat/completions` の応答を受ける際に `choices[].finish_reason` を読んでおらず、`StopReason` は
既定値 `null` のまま返っていた。

結果として、**同じ応答契約がプロバイダによって意味を持ったり持たなかったりする**。呼び出し側
（`RagOrchestrator` の拒否注記・`LlmGatewayDiagramCoder` の `llm-refused`・AST `trade-decision`）は
既に `stopReason` を見る実装へ移行しており、ティアA/C 経路では #379 が解こうとした混同
（拒否・上限到達・正常終了の区別不能）がそのまま残る。

とりわけ **`content_filter`（OpenAI の安全性フィルタ停止）で本文破棄が効かない**点が重い。
[[IADR-0104]] は「拒否直前の断片を下流の判断材料にしない」ことを fail-safe として決めており、
Claude 経路だけその保証があり、セルフホスト／Copilot 経路には無い、という非対称が生まれる。

両エンドポイントは既定 `Enabled=false`（[[IADR-0022]]）だが、設定 1 つで有効化できる。
有効化した瞬間に表面化する欠落であり、有効化前に塞ぐ方が安い。

### 語彙の差

| OpenAI `finish_reason` | 意味 | 正準語彙（`CompletionStopReasons`） |
| --- | --- | --- |
| `stop` | 正常終了（停止トークン到達を含む） | `end_turn` |
| `length` | `max_tokens` 到達で打ち切り | `max_tokens` |
| `content_filter` | コンテンツフィルタによる停止 | `refusal` |
| `tool_calls` | ツール呼び出しで停止 | `tool_use` |
| `function_call` | 旧 function calling（OpenAI で非推奨） | `tool_use` |

## 検討した選択肢

1. **プロバイダ境界で正準語彙（Anthropic 由来）へ正規化する（採用）** — 応答契約の語彙を 1 つに保つ。
   呼び出し側は `CompletionStopReasons.IsRefusal` / `IsMaxTokens` だけを知っていればよく、
   プロバイダが増えても判定ロジックが増えない。本文破棄の fail-safe も一箇所（プロバイダ）で揃う。
2. `finish_reason` を素通しし、呼び出し側で両方の語彙を吸収する — 契約は「プロバイダの生の語彙」になり、
   **呼び出し側の数だけ写像が複製**される（`RagOrchestrator` / `LlmGatewayDiagramCoder` / AST 取引判断・
   報告書生成。別リポジトリを含む）。1 箇所でも追随漏れがあれば `content_filter` が拒否として扱われず、
   [[IADR-0104]] の fail-safe が破れる。issue #394 が指摘する「プロバイダによって契約の意味が変わる」
   状態を、呼び出し側へ移し替えるだけである。採らない。
3. `StopReason` を enum 化して両語彙を型で表す — 未知値が既定値へ黙って落ちる。[[IADR-0104]] が
   文字列型を選んだ理由（語彙の増加を弾かず透過してログに残す）に反する。採らない。
4. 正規化に加えて生の `finish_reason` も契約へ載せる（`rawStopReason` 等） — 監査の情報量は増えるが、
   共有契約に OpenAI 固有の概念が漏れ、全呼び出し側に「どちらを見るべきか」の判断を強いる。
   未知値は原文透過されるため、正規化できない値の情報は失われない。現時点では過剰と判断し採らない。

## 決定

1. **写像はゲートウェイ側（`LlmGateway.Api/Composable/Adapters/OpenAiFinishReasons`）に置く**。
   共有契約（`Platform.Shared.Contracts`）へ OpenAI の語彙定数を持ち込まない。OpenAI 互換の
   `finish_reason` は**トランスポートの関心事**であり、契約の関心事ではない。
2. 写像は上表のとおり。**大小文字非依存**で比較する（`CompletionStopReasons.IsRefusal` と同じ方針）。
3. **未知値は既定値へ潰さず原文のまま透過**し、プロバイダが **warn ログ**へ記録する
   （どのエンドポイント・どのモデルで未知語彙が来たかを残す）。
4. `finish_reason` の**欠落・`null` は `StopReason=null`**（[[IADR-0104]] の「未対応プロバイダは null」と
   同じ状態）。これは未知語彙ではないため warn ログを出さない（正常系のログを汚さない）。
5. **`content_filter` → `refusal` のときは本文を破棄**する（`Text` を空にする）。[[IADR-0104]] §決定 3 の
   「refusal のときだけ本文を破棄する」をプロバイダ横断で一貫させる。`length` → `max_tokens` の
   途中結果は**破棄しない**（同じく [[IADR-0104]] と一貫）。
6. **ストリーミングは個別実装を追加しない**。両プロバイダは `ILlmProvider` の既定 `StreamAsync`
   （`CompleteAsync` を単一チャンクへ縮退。[[IADR-0037]]）を使うため、正規化後の `StopReason` は
   最終チャンクへ自動的に載る。
7. 回帰は T-20（`OpenAiFinishReasonTests` / `OpenAiProviderStopReasonTests`）で固定する。

## 理由

- **契約の語彙を 1 つに保つ**ことが、呼び出し側を増やしても壊れない唯一の形である。ゲートウェイは
  「呼び出し先の差を吸収して統一契約で返す」ために存在する（ADR-0010）のだから、語彙の差の吸収も
  その責務に含まれる。
- **fail-safe の非対称を消す**。`content_filter` を `refusal` へ写像し本文も破棄することで、
  「AST 取引判断が拒否された断片を根拠に売買判断へ進む」（[[IADR-0104]] が塞いだ穴）が
  セルフホスト／Copilot 経路でも塞がる。
- **未知値の透過は [[IADR-0104]] の設計判断の踏襲**である。`stop_reason` を文字列にしたのと同じ理由で、
  写像表に無い値を `null` や `end_turn` へ倒さない（倒すと「正常終了」に見えてしまい最悪である）。
- 写像をゲートウェイに閉じることで、**共有契約は変更ゼロ**になり、AST を含む別リポジトリの
  呼び出し側は無改修で恩恵を受ける。

## 結果

- 良い影響: ティアA/C 経路でも拒否・上限到達・正常終了が区別できる。`content_filter` の本文破棄が
  効き、fail-safe がプロバイダ横断で揃う。共有契約・呼び出し側は無改修。
- 悪い影響 / トレードオフ:
  - **生の `finish_reason` は応答契約に残らない**（正準語彙へ写像される）。既知語彙については
    どのプロバイダが返したかを応答から復元できない。必要になれば選択肢 4 を再検討する。
    未知値は原文が透過されるため情報は失われない。
  - `stop` → `end_turn` の写像は**厳密には同義ではない**（OpenAI の `stop` は停止シーケンス到達も含み、
    Anthropic はそれを `stop_sequence` として区別する）。区別が必要な用途は現状なく、
    呼び出し側の分岐は `refusal` / `max_tokens` の判定のみであるため実害はない。
  - 両プロバイダに `ILogger` 依存が増える（未知値の記録のため）。
- フォローアップ:
  1. **拒否率の可観測化**（[[IADR-0104]] §フォローアップ 3 /
     [#395](https://github.com/endazon/microservices-platform/issues/395)）。本 IADR で
     `content_filter` も `refusal` として観測できるようになるため、メトリクスはプロバイダ横断で意味を持つ。
  2. セルフホスト／Copilot エンドポイントを実際に有効化する際、**実応答の `finish_reason` 語彙**を
     一度実測して写像表の網羅性を確認する（vLLM 等は独自値を返し得る。未知値は warn ログに出る）。

## 関連

- Supersedes: なし（[[IADR-0104]] §フォローアップ 2 を消化する。決定内容は不変）
- Superseded by: なし
- 関連要求 / UC: FR-11（LLM 送信可否の統制）、FR-04 / FR-12（呼び出し側）、UC-01 / UC-02
- 関連 IADR: [[IADR-0104]]（`stopReason` の契約と refusal の本文破棄）、[[IADR-0022]]（ティアA/C 経路）、
  [[IADR-0037]]（既定 `StreamAsync`）、[[IADR-0101]]（`max_tokens` 到達の背景）
