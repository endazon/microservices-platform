---
title: LLM ゲートウェイが本文に使わなかった 2 個目以降の text ブロックを、計器（型 text）と Warning に残す（#2000）
type: spec
status: done
related_ids: [FR-11, NFR-19, ADR-0010, IADR-0563, IADR-0562, IADR-0535, IADR-0114]
author: claude
created: 2026-10-20
updated: 2026-10-20
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0010（LLM ゲートウェイ）
issue: "#2000"
---

# 仕様書: 2 個目以降の text ブロックを黙って捨てない（#2000）

> 本仕様書は実装着手前に作成した（着手 2026-10-20）。実装の基点は MSP `origin/develop` `fe06bdee`。
> 呼び出し側の前提は AST `origin/develop`（submodule の pin `bc2ab98`）の `TradeDecisionParser` で確かめた。判断は IADR-0563。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-11（LLM 送信先の切替・ゲートウェイ）
- 非機能: NFR-19（可観測性）
- 関連 ADR: ADR-0010（LLM ゲートウェイ）
- 関連 IADR: IADR-0562 決定 5（本文は先頭の text ブロック）・IADR-0535（ログ段階と計器）・IADR-0114 決定 5（ログは本文を載せない）

## 目的・背景

`ClaudeProvider.ExtractText` は先頭の text ブロックだけを本文に使い、2 個目以降の text ブロックを**ログにも計器にも残さず**捨てている。
AST#1332（一次判断の出力が `NoJsonObject`）の原因の候補「JSON が 2 個目の text ブロックにある」を、どちらのリポのログでも確かめられない。

## 母集合（規則 9）

- 誤りの側の文字列 `content_blocks_dropped` / `content_block_type` / `3 値` で全文書を走査した（`git grep`）。追随先:
  `AnthropicContentBlockMetrics.cs`・`ClaudeProvider.cs`・`ClaudeProviderContentBlockLogTests.cs`・
  `docs/observability/llm-completion-metrics.md`・`docs/functional/FR-11_llm-egress-routing.md`・`docs/tests/FR-11_llm-egress-routing.md`。
- 除外: `.ai-context/specs/` の既存 2 本（#1917・#1749）と IADR-0114 / 0535 / 0562（凍結記録。値域が 4 値になったことは IADR-0563 が持つ）。
- 計器・型属性を引くダッシュボード・警報・Grafana の規則は無い（`deploy/` を走査して 0 件）。値の追加で既存の系列は変わらない。

## 呼び出し側の前提（連結の可否）

| 呼び出し側 | 経路 | 本文の解析 |
| --- | --- | --- |
| AST 一次・二次判断（`TradeDecisionParser.ReadEnvelope`） | 一括 | 本文の**最初の `{`** から釣り合う `}` までを 1 個の JSON として読む |
| MSP 再順位付け（`ClaudeSearchReranker`） | 一括 | 本文を JSON として読む |
| MSP 図の符号化・関係の提案・クラスタ要約 | 一括 | 本文を 1 個の応答として読む |

連結すると、先頭のブロックの散文に `{` があれば JSON の抽出位置が変わる（解析の前提が変わる）。どれが原因かを確かめる計器が先に要る。**連結しない**（IADR-0563）。

## 受け入れ基準

- [x] text ブロックが 2 個以上ある応答で、本文は先頭の 1 個のまま（連結しない）。
- [x] 捨てた text ブロックの件数を計器 `llm.anthropic.content_blocks_dropped{llm.content_block_type="text"}` へ数える。
- [x] Warning を 1 行出す。構造化値は `UsedTextLength`・`DroppedTextBlocks`・`DroppedTextLengths`。本文の文字列は載せない。
- [x] text ブロックが 1 個だけなら `text` を数えない（陰性対照）。
- [x] 型属性は 4 値（`thinking` / `redacted_thinking` / `text` / `other`）に閉じる。
- [x] 変異: 記録を外すと T-44 が赤。

## 変異の結果

| 変異 | 結果 |
| --- | --- |
| `ExtractText` の記録の分岐を `&& false` で殺す | `二個目以降のtextブロックを捨てたときは件数を計器とWarningに残す` が赤（14 件中 1 件） |

## 範囲外

- ストリーム（`StreamAsync`）は従前どおり数えない。ストリームは全ブロックの `text_delta` を区切りなしで流す（実質は連結）。一括との非対称は IADR-0563 §残るもの。
