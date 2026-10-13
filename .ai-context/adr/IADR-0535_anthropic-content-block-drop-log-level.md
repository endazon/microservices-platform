---
title: IADR-0535 Anthropic 応答の content ブロック除去のログを「想定内は Debug・想定外だけ Warning」に分け、除去件数を計器で数える（IADR-0114 決定 5 を改める）
type: impl-adr
status: Accepted
related_ids: [FR-11, NFR-19, ADR-0006, ADR-0010, IADR-0110, IADR-0114, IADR-0394, IADR-0504, IADR-0531]
author: claude
created: 2026-10-13
updated: 2026-10-13
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0010（LLM ゲートウェイ）
  - planning:projects/microservices-platform/07_adr/ADR-0006（可観測性）
related_specs:
  - ../specs/20261013_1917_thinking-strip-log-level.md
---

# IADR-0535: content ブロック除去のログ段階を分け、件数を計器で数える

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: Accepted
- 日付: 2026-10-13
- 決定者: Claude（実装）／起点は #1917（2026-10-12 の PoC の監査）

## 起点・関連

- 起点 issue: [#1917](https://github.com/endazon/microservices-platform/issues/1917)。
- 関連する計画書 ID: FR-11（LLM 送信先の切替）・ADR-0010（LLM ゲートウェイ）・ADR-0006（可観測性）・NFR-19（運用ログ）。
- 一部を改める実装判断: [IADR-0114](./IADR-0114_anthropic-unknown-content-block-sanitizing.md) **決定 5**
  （「除去したブロック型は必ず WARN で記録する」）。決定 1〜4・6 は変えない。
- 前提: [IADR-0531](./IADR-0531_claude-5-5-model-migration.md)（5.5 系は 3 モデルとも thinking を無効にできず、Haiku も thinking を返す）。
- 計器の規律: [IADR-0110](./IADR-0110_llm-completion-stop-reason-metrics.md)（属性の値域を閉じる）・
  [IADR-0394](./IADR-0394_meter-probe-instance-scoping.md)（試験の probe は Meter のインスタンスで絞る）。
- 作業仕様書: [20261013_1917_thinking-strip-log-level](../specs/20261013_1917_thinking-strip-log-level.md)。

## コンテキストと課題

`AnthropicResponseSanitizingHandler` は、SDK が解釈できない content ブロックを除去するたびに Warning を 1 行出していた
（IADR-0114 決定 5）。意図は「無言で捨てると応答形状の変化に気づけないまま本文が空になり得る」ことへの備えである。

5.5 系への切替（IADR-0531）で、thinking は**すべての割当モデルで無効にできない通常の応答の形**になった。
2026-10-12 13:00Z〜20:00Z の PoC では Warning が 685 行出た（haiku-5-5 は 682/682、sonnet-5-5 は 2/47 が thinking を含む）。
Warning の水準が毎応答の通常の形で埋まると、決定 5 が拾いたかった「形の変化」そのものが埋もれる。

決定 5 は「想定内の除去（thinking）」と「想定外の除去（未知型・本文の喪失）」を区別していなかった。

## 検討した選択肢

1. **条件で段階を分け、件数は計器で数える（採用）**。
2. **起動後の初回だけ Information、以後 Debug（棄却）** —— 毎応答の通常の形を起動ごとに 1 行知らせても判断材料にならない。
   件数の傾向は計器のほうが読める。
3. **`LogOccurrenceThrottle`（5 分ごとの要約 1 行）で抑える（棄却）** —— 想定内の事象を Warning の水準に残す理由が無い。
   抑制は「同じ異常が続く」ときの道具であり（IADR-0504 追記）、通常の形に当てるものではない。
4. **thinking を許可リストに足し、除去をやめる（棄却）** —— SDK 4.0.0 は thinking で応答全体を落とす（IADR-0114）。

## 決定

**決定 1: 段階は次の 3 条件で決める。**

| 条件 | 段階 |
| --- | --- |
| 除去が `thinking` / `redacted_thinking` だけで、既知ブロックが 1 個以上残る | Debug |
| 除去した中に上記以外の型（`server_tool_use`・将来型・型名の無い要素）がある | Warning（想定外の型名を構造化値 `UnexpectedTypes` に載せる） |
| 除去の結果、既知ブロックが 1 個も残らない（content 空への縮退。IADR-0114 決定 6） | Warning（型を問わない） |

想定内の型は `AnthropicContentBlockMetrics.ExpectedDroppedTypes` の 1 か所に置く。ログには型名だけを載せ、本文は載せない（IADR-0114 決定 5 の機微の線は維持）。

**決定 2: 除去件数を計器 `llm.anthropic.content_blocks_dropped` で数える。**
既存の Meter `microservices-platform.llm-gateway`（`LlmCompletionMetrics.MeterName`）に `Counter<long>`（単位 `{block}`）を足す。
属性 `llm.content_block_type` は `thinking` / `redacted_thinking` / `other` の 3 値に閉じる（型名は上流が決める非有界な値。
原文は Warning 側に載る）。Prometheus 側の名前は `llm_anthropic_content_blocks_dropped_total`。

**決定 3: 計器はハンドラの必須の引数にする。** 省略可能にすると本番の配線で黙って欠ける。
`Program.cs` が単一インスタンスを登録し、`AnthropicHttpClient.Create` を経てハンドラへ渡す。

## 理由

- 「傾向は計器、個別はログ」（IADR-0110）。毎応答の通常の形は件数で見れば足り、ログの Warning は人が読むべき事象に空けておく。
- 想定外の 2 形（未知型・本文の喪失）は、決定 5 が本来拾いたかった「形の変化」である。これらは引き続き Warning で出る。

## 結果

- PoC の量では Warning は 0 行になる見込み（thinking ＋ 本文の応答だけなら）。Debug は既定のログ段階では出ない。
- `other` の増加は、API が新しいブロック型を返し始めたことを表す（Warning と同時に起きる）。
- 回帰は **T-41**（`AnthropicResponseSanitizingHandlerLogLevelTests`）で固定する。T-24 は不変。
- 稼働中環境への操作なし。応答本文の扱い（除去・素通し・空縮退）は不変。

## 残余リスク

- thinking を 1 個も含まない応答が増えても、ログでも計器でも見えない（除去が起きないため）。それは除去の問題ではなく、必要なら別の計器で扱う。
