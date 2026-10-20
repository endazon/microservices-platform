---
title: IADR-0564 LLM ゲートウェイは 2 個目以降の text ブロックを本文へ連結せず、捨てた件数を計器 content_blocks_dropped の型 text と Warning に残す
type: impl-adr
status: Accepted
related_ids: [FR-11, NFR-19, ADR-0010, IADR-0562, IADR-0535, IADR-0114]
author: claude
created: 2026-10-20
updated: 2026-10-20
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0010（LLM ゲートウェイ）
related_specs:
  - ../specs/20261020_2000_extra-text-block-drop-count.md
---

# IADR-0564: 2 個目以降の text ブロックは連結せず、件数を計器と Warning に残す（#2000）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-20
- 決定者: claude（#2000）

## 起点・関連

- 起点 issue: #2000（Refs #1749・AST#1332）。
- 関連する計画書 ID: FR-11・NFR-19。計画 ADR: ADR-0010（LLM ゲートウェイ）。
- 改める実装判断: [IADR-0562](./IADR-0562_anthropic-official-sdk-migration.md) 決定 5（計器の値域 3 値 → 4 値）・[IADR-0535](./IADR-0535_anthropic-content-block-drop-log-level.md)（値域）。
- 変えない実装判断: [IADR-0114](./IADR-0114_anthropic-unknown-content-block-sanitizing.md) 決定 5（ログは型名・件数だけで本文を載せない）。
- 作業仕様書: [20261020_2000_extra-text-block-drop-count](../specs/20261020_2000_extra-text-block-drop-count.md)（呼び出し側の前提の表・母集合・変異）。

## コンテキストと課題

`ClaudeProvider.ExtractText` は本文を先頭の text ブロックから取り、2 個目以降の text ブロックを記録せずに捨てていた。
text 以外の型は計器 `llm.anthropic.content_blocks_dropped` で数えているが、text は数えていない。
AST#1332 の候補「JSON が 2 個目の text ブロックにある」を確かめる手段が無い。

## 検討した選択肢

1. **連結する**（全 text ブロックを本文へ）。ストリームと形が揃う。しかし呼び出し側の解析の前提が変わる。AST の判断は本文の最初の `{` から JSON を抜き出すので、先頭のブロックの散文に `{` があると抽出位置が変わる。MSP の再順位付け・図の符号化等も本文を 1 個の応答として読む。原因が確かめられていない段階で、全呼び出し側の入力を変えることになる。
2. **記録だけする**（連結しない）。挙動は変えず、件数と文字数を残す。AST#1332 の候補を、計器とログで確かめられるようになる。
3. 別の計器を新設する。件数は既存の「本文に使わず捨てたブロック」の意味にそのまま入るので、新設は系列を割るだけである。

## 決定

### 決定 1 — 連結しない。本文は先頭の text ブロックのまま

選択肢 2 を採る。連結の可否は、計器で「2 個目の text ブロックが実際に来ているか」を確かめてから、呼び出し側の解析とあわせて決める（AST#1332 の調査に委ねる）。

### 決定 2 — 件数は既存の計器の型 `text` で数える

- `llm.anthropic.content_blocks_dropped` の属性 `llm.content_block_type` に値 `text` を足す（値域は 4 値: `thinking` / `redacted_thinking` / `text` / `other`）。2 個目以降の text ブロック 1 個につき 1。
- `text` は想定内の型（`ExpectedDroppedTypes`）に入れない。既存の 3 条件のログ（IADR-0535）は text 以外の型だけで判定し、変えない。

### 決定 3 — Warning を 1 行出す。載せるのは件数と文字数だけ

- 本文を捨てているので常に Warning。構造化値は `UsedTextLength`（本文に使った先頭のブロックの文字数）・`DroppedTextBlocks`（捨てた個数）・`DroppedTextLengths`（捨てた各ブロックの文字数）。
- 本文の文字列は載せない（IADR-0114 決定 5）。文字数の偏り（先頭が短く 2 個目が長い）で「本文が 2 個目にある」を推定できる。
- 固定: T-44（`ClaudeProviderContentBlockLogTests`）。

## 結果

- 既存の系列（`thinking` / `redacted_thinking` / `other`）は変わらない。ダッシュボード・警報で型属性を引くものは無い（作業仕様書の母集合）。
- 呼び出し側の本文は変わらない。

### 残るもの

- **ストリームとの非対称**: `StreamAsync` は全ブロックの `text_delta` を区切りなしで流す（実質は連結）。一括とストリームで本文の取り方が違う。ストリームの利用者（RAG の回答）は散文を表示するだけで JSON を解析しないので、本 IADR では揃えない。
- 連結するかは、計器 `text` の実測（AST#1332）を見てから決め直す。

## 関連

- #2000・#1749
- AST#1332（調査の起点）
