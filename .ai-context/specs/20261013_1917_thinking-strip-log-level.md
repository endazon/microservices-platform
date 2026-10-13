---
title: 作業仕様書 — LLM ゲートウェイの thinking 除去のログを Debug へ下げ、想定外の形だけを Warning に残し、除去件数を計器で数える（#1917）
type: spec
status: done
related_ids: [FR-11, NFR-19, ADR-0006, ADR-0010, IADR-0110, IADR-0114, IADR-0531, IADR-0535]
author: claude
created: 2026-10-13
updated: 2026-10-13
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0010（LLM ゲートウェイ）
  - planning:projects/microservices-platform/07_adr/ADR-0006（可観測性）
issue: "#1917"
---

# 作業仕様書 — thinking 除去のログ段階の是正（#1917）

> 本仕様書は実装着手前に作成した（着手 2026-10-13）。判断の記録は **IADR-0535** に置く。
> 基点は MSP `origin/develop` `6ca33d75`。

## 起点となる計画書（トレーサビリティ）

- 起点 issue: **#1917**（2026-10-12 13:00Z〜20:00Z の PoC で `AnthropicResponseSanitizingHandler` の Warning が 685 行。
  haiku-5-5 の応答は 682/682、sonnet-5-5 は 2/47 が thinking を含んだ）。
- 計画: **FR-11**（LLM 送信先の切替）・**ADR-0010**（LLM ゲートウェイ）・**ADR-0006**（可観測性）・NFR-19（運用ログの量）。
  計画はログ段階と計器の名前を定めていない（実装の細目）。計画への環流は要らない。
- 改める実装判断: [IADR-0114](../adr/IADR-0114_anthropic-unknown-content-block-sanitizing.md) **決定 5**（「除去したブロック型は必ず WARN で記録する」）。
  他の決定（許可リスト・接合点・対象・素通し・空縮退）は変えない。

## 経緯の調査（手順 1）

- Warning を入れたのは #431（`987dc867`。IADR-0114）。当時の意図は決定 5 のとおり
  「無言で捨てるとモデル側の応答形状の変化に気づけないまま本文が空になり得る」。
  当時の割当（opus-5 / sonnet-5 / fable-5）も thinking が既定で有効だったが、**thinking の除去を「想定内」と「想定外」に分けていなかった**。
- 2026-10-10 の 5.5 系への切替（#1875・IADR-0531）で **haiku-5-5 も thinking を返す**ようになり、軽量用途（rerank 等）の呼び出し数が
  そのまま Warning の行数になった。5.5 系の thinking は無効にできない（IADR-0531）。よって **thinking / redacted_thinking の除去は通常の応答の形**である。
- 条件（現状）: 2xx・`application/json`・`content[]` に許可リスト外のブロックが 1 個以上 → 型を問わず Warning 1 行。

## 設計（正は IADR-0535）

| 条件 | ログ段階 | 計器 |
| --- | --- | --- |
| 除去したのが `thinking` / `redacted_thinking` だけで、既知ブロックが 1 個以上残る | **Debug** | 加算（型ごと） |
| 除去した中に上記以外（`server_tool_use`・将来型・型名の無い要素） | **Warning**（想定外の型の名前を載せる） | 加算（`other`） |
| 除去の結果、`content` が空になった（既知ブロックが 1 個も残らない） | **Warning**（型を問わない） | 加算 |
| 除去なし | 出さない | 加算しない |

- 計器: 既存の Meter `microservices-platform.llm-gateway`（`LlmCompletionMetrics.MeterName`。`Program.cs` の `AddMeter` 済み）に
  `llm.anthropic.content_blocks_dropped`（`Counter<long>`・単位 `{block}`）を足す。属性 `llm.content_block_type` は
  `thinking` / `redacted_thinking` / `other` の 3 値に閉じる（IADR-0110 の値域を閉じる規律）。
- 実装: `Common/Observability/AnthropicContentBlockMetrics.cs`（新規）。`AnthropicResponseSanitizingHandler` と
  `AnthropicHttpClient.Create` が計器を受ける。`Program.cs` で単一インスタンスを登録して渡す。
- サニタイザ: `TrySanitize` に「残った既知ブロック数」を返す多重定義を足す（既存の 3 引数は温存）。
- Warning の文言は型名のみ（本文は載せない。IADR-0114 決定 5 の機微の線は維持）。

## 母集合（規則 9・10）

走査語: `除去.{0,40}(warn|警告)` / `(warn|警告).{0,40}除去` / `SDK 未対応の content`（`.ai-context/specs`・`superpowers` を除く全追跡ファイル）。

| 該当 | 対応 |
| --- | --- |
| `.ai-context/adr/IADR-0114_…` 決定 5 | 本文は書き換えず、日付つき追記ブロックで IADR-0535 が決定 5 を改めたことを書く |
| `.ai-context/adr/README.md` IADR-0114 行 | 一覧の要約は当時の記録。IADR-0535 の行を追加する（0114 行の要約は変えない） |
| `docs/functional/FR-11_llm-egress-routing.md` §応答 content ブロック | 「warn ログに残す」を条件別の段階と計器へ書き直す |
| `docs/tests/FR-11_llm-egress-routing.md` T-24 | 「WARN に残す」を改め、新しい **T-41** を足す |
| `AnthropicResponseSanitizingHandler.cs` のコメント | 書き直す |
| `docs/observability/llm-completion-metrics.md` | 計器の一覧に新しい計器の節を足す |

除外: `.ai-context/specs/20260801_issue-290_…`（確定済みの作業仕様書。凍結）。

## 試験（T-41）

| 観点 | 期待 |
| --- | --- |
| thinking ＋ text | Debug 1 行・Warning 0 行・計器 `thinking`=1 |
| redacted_thinking ＋ thinking ＋ text | Debug・Warning 0・`thinking`=1・`redacted_thinking`=1 |
| server_tool_use ＋ text | Warning 1 行（型名を含む）・計器 `other`=1 |
| thinking だけ（空へ縮退） | Warning 1 行・計器 `thinking`=1 |
| 既知型だけ | ログ 0 行・計器 0 |

ログ段階は既存の流儀（`RecordingLoggerFactory` で `Level` を表明。`AnthropicHttpClientTests`）、
計器は Meter の**インスタンス**で絞る probe（IADR-0394 決定 1。`LlmUsageMetricsTests`）に合わせる。

## 受け入れ基準

- [ ] 上表の条件ごとのログ段階と計器の加算が試験で固定されている。
- [ ] 既存の T-24 / T-34 が緑のまま。
- [ ] `dotnet test`（LlmGateway を含む platform ユニット）・`dotnet format --verify-no-changes`・文書系の検査器・`scripts.test.js` が緑。
