---
title: IADR-0613 取り込みの埋め込みの総枠は、構成で与えていなければ受け口の期限とコレクション数から縮めて既定とする（上限 300 秒・余白 5 秒）
type: impl-adr
status: Accepted
related_ids: [FR-02, UC-04, ADR-0027, ADR-0016, ADR-0127, IADR-0595, IADR-0478, IADR-0313, IADR-0085, IADR-0497]
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-02）
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md
related_specs:
  - ../specs/20261024_2066_ingestion-budget-opt-in-collections.md
---

# IADR-0613: 取り込みの総枠の既定をコレクション数に合わせる

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-24
- 決定者: claude（#2066）

## 起点・関連

- 関連する計画書 ID: FR-02（取り込み）、UC-04、計画 ADR-0027（再試行・デッドレター）、ADR-0016 / ADR-0127（コレクションの分離・語彙索引）。
- 前提とする実装 ADR: [IADR-0478](IADR-0478_consumer-outbound-call-timeouts-under-wolverine-execution-timeout.md)（時間の上限の式・決定 7 の連鎖）、
  [IADR-0595](IADR-0595_ingestion-index-version-and-write-then-delete.md) 決定 5（式に版の印と旧版の削除を足した）、
  [IADR-0313](IADR-0313_deterministic-local-embedding-for-search-gate.md)（決定的ローカル埋め込み）、[IADR-0085](IADR-0085_selfhosted-embedding-optin-deploy.md)（Ruri の opt-in）、
  [IADR-0497](IADR-0497_high-confidentiality-lexical-index-vectorless-collection.md)（語彙索引の 1 本）。
- 作業仕様書: [20261024_2066_ingestion-budget-opt-in-collections](../specs/20261024_2066_ingestion-budget-opt-in-collections.md)。

## コンテキストと課題

- IADR-0595 決定 5 の後、受け口の最悪の所要時間は `20c ＋ 390` 秒（c はコレクション数。総枠 300 秒を含む）になった。既定の c＝2 で 430 秒、受け口の期限の既定は 435 秒。
- chart の opt-in はコレクションを足す（決定的ローカル埋め込み・Ruri。最大 c＝4）。c＝3 で 450 秒、c＝4 で 470 秒となり、
  取り込みは起動時の検査で落ちる（develop の integration-stack が #2149 から連続で赤。`LOCALEMBED=1` で c＝3）。
- 受け口の期限は IADR-0478 決定 7（4 × 期限 ＋ 42 ＜ consumer_timeout 1800）から 439 秒が上限で、総枠 300 秒のままでは**期限をどう構成しても c≥3 は起動できない**。

## 決定

1. **`Ingestion:EmbeddingBudgetSeconds` を構成で与えていなければ**、総枠の既定を
   `min(300, 受け口の期限 − (2 × c × Qdrant ＋ 3 × Qdrant ＋ 本文 ＋ 埋め込み 1 回 ＋ Qdrant 1 回) − 5 秒)`（下限 1 秒）とする。
   既定の値では c＝2: 300 秒（従前と同じ）／ c＝3: 280 秒／ c＝4: 260 秒。
2. 余白 5 秒（`DefaultBudgetHeadroomSeconds`）は既定の構成の余り（435 − 430）と同じ値とし、既定の配備の挙動を変えない。
3. **構成で与えた総枠は縮めない。** 収まらなければ従前どおり起動を止める（運用者の値を黙って変えない）。
4. 受け口の期限を構成で上げても、総枠の既定は 300 秒を超えない（頭打ち）。総枠を広げるのは従前どおり明示の構成で行う。

## 退けた案

- 既定の総枠を全体で 260 秒へ下げる: 既定の 2 本の配備まで収まる文書を狭め、警報の説明文などの「既定 300 秒」を 6 か所以上追随させる。
- chart で opt-in のときだけ総枠を描く: 式を helm へ複写し、コードの既定と二重に持つ（IADR-0478 の「値は chart へ足さず、コードの既定で持つ」に反する）。
- 受け口の期限・ブローカの consumer_timeout を上げる: 前者は連鎖の検査で 439 秒が上限。後者は共有ブローカの全利用者に効き、作り直しが要る。
- 先に全コレクションから消した取り込みでは書き終えた後の削除を省く: IADR-0595 決定 4（並行の取り込みの防御）を緩める。

## 結果

- 良い: opt-in でコレクションを足した配備（integration-stack・Ruri を有効にした配備）が既定の構成のまま起動する。既定の 2 本の配備は 1 ビットも変わらない。
- 悪い（受容）: opt-in の配備では総枠が 20 秒／本だけ縮む（c＝3 で約 7%）。総枠を使い切った文書は従前どおり警報 `IngestionBudgetExhausted` が鳴り、明示の構成で広げられる。
  縮めた値はログに出さない（起動時の値は DI の `IngestionTimeouts` が持つ）。
