---
title: IADR-0671 LLM ゲートウェイは呼び手が分けた接頭辞を user の先頭の text ブロックにし、その末尾に 5 分のキャッシュの区切りを置く。InputTokens はプロンプト全体のまま、キャッシュの書き・読みを「うち」として返し、単価の区分ごとに計上する（#2396）
type: impl-adr
status: Accepted
related_ids: [FR-11, FR-10, NFR, ADR-0010, ADR-0022, ADR-0044, IADR-0531, IADR-0562, IADR-0601]
author: claude
created: 2026-11-05
updated: 2026-11-05
related_specs:
  - ../specs/20261105_2396_llm-gateway-prompt-caching.md
---

# IADR-0671: LLM ゲートウェイのプロンプトキャッシュと、キャッシュの読み・書きの計上（#2396）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-05
- 決定者: claude（#2396。コーディネーターの指示 2026-11-05）

## 起点・関連

- 起点 issue: #2396（PoC セッション・2026-11-05 のコスト監査）。
- 前提:
  - 計画 ADR-0022（プロンプトキャッシュの最小キャッシュ長）・ADR-0044 決定 3（換算はゲートウェイ。無音で 0 円にしない）。
  - IADR-0531 決定 2（Haiku 5.5 の 2 段の単価。判定量の見直しを予告していた）。
  - IADR-0562（公式 SDK `Anthropic` 12.53.0）・IADR-0601（逐次の判明分の計上）。
- 作業仕様書: `.ai-context/specs/20261105_2396_llm-gateway-prompt-caching.md`。

## コンテキスト

- `ClaudeProvider` は user の 1 件（文字列の content）だけを送り、`cache_control` を一度も付けていなかった。
- AST の取引判断は 1 回の場でスクリーニング 318 回・本判断 49 回を呼ぶ。プロンプトの前半（固定の前文・当日の方針・監視銘柄）は当日のうち変わらず、
  呼び出しの間隔は 5 分より短い。入力の費用は 1 回の場で約 154 円（issue の実測）。
- 一次情報（2026-11-05 に確認。作業仕様書の表）:
  - `cache_control` は tools・`system`・`messages[].content` の text ブロックに置ける。区切りは最大 4 個。描画順は tools → system → messages。
  - 5.5 系 3 モデルの最小長は 512 トークン。短い接頭辞はエラーにならず、キャッシュされないだけ。
  - TTL は既定 5 分、`"ttl":"1h"` で 1 時間（書き 2 倍）。
  - 応答の `input_tokens` は**最後の区切りの後ろの残りだけ**で、全体は `input + cache_creation + cache_read`。
  - 書き（5 分）は入力の 1.25 倍。読みは Opus 5.5・Sonnet 5.5 が 0.05 倍、Haiku 5.5 が 0.1 倍。
  - Haiku 5.5 の段の判定量は、キャッシュの読み・書きを含むプロンプト全体。
- キャッシュを使い始めると、`usage.input_tokens` だけを読む今の計上は次のように崩れる。
  - 書き（1.25 倍）・読みが金額から落ち、過小になる。
  - Haiku 5.5 の段を残りだけで選ぶので、上段の要求を下段で数える。
  - 応答の `InputTokens` の意味が「全体」から「残り」へ黙って変わる。AST は `InputTokens × 入力単価` と `InputTokens` による段の選択で費用を出しているので、AST の費用も過小になる。

## 決定

### 決定 1 — 接頭辞は呼び手が分けて渡し、user の先頭の text ブロックにする（system へは移さない）

- 要求に任意の `CacheablePrefix` を足す（REST `cacheablePrefix`・proto `CompleteRequest.cacheable_prefix = 6`）。
  - モデルが読む全文は `CacheablePrefix + Prompt` とし、区切り文字は足さない。
  - null・空文字は「無い」として扱い、従前と同じ形（文字列の content・`cache_control` なし）で送る。
- `ClaudeProvider` は、接頭辞があるときだけ user の 1 件の content を text ブロック 2 個にする。
  - 1 個目は接頭辞で、`cache_control: {"type":"ephemeral"}`（5 分）を付ける。
  - 2 個目は残りで、区切りは付けない。残りが空なら送らない。
- model・max_tokens・`output_config.effort`・messages の件数と役割は変えない（IADR-0531 決定 4 の要求本文の固定も守る）。
- キャッシュの区切りを持たないプロバイダ（SelfHosted・Copilot）は、`FullPrompt` を従前と同じ 1 つの文字列で送る。

### 決定 2 — TTL は 5 分だけにし、最小長の判定はゲートウェイで持たない

- 呼び出しの間隔が 5 分より短く、読むたびに寿命が延びるので、1 時間の TTL は書きの単価を 2 倍にするだけである。
- 最小長（512 トークン）に満たない接頭辞も弾かない。
  - 提供元はエラーにせず、キャッシュしないだけで、書きの割増も掛からない（`cache_creation_input_tokens: 0`）。
  - モデルごとの最小長をコードに写すと、提供元の改定に追随する対象が 1 つ増える。
- 自動キャッシュ（トップレベル `cache_control`）は使わない。区切りが残り（銘柄固有部）の後ろに置かれ、毎回書きの割増だけを払う形になるためである。

### 決定 3 — `InputTokens` はプロンプト全体のまま。キャッシュの書き・読みは「うち」として足す

- `CompletionResult` / `CompletionChunk` / `CompletionApiResponse` / `CompletionStreamEvent` の `InputTokens` を、
  `input_tokens + cache_creation_input_tokens + cache_read_input_tokens` と定義する。
- 「うち」の書き・読みを `CacheCreationInputTokens` / `CacheReadInputTokens` として足す。
  - proto は `CompleteResponse` 11・12、`CompletionStreamEvent` 12・13。
  - 末尾追加で既定は 0 である。
- 理由: 既存の呼び手（AST の費用計上）は、`InputTokens` を全体として読んで段を選び、入力単価を掛けている。
  - 全体の意味を保てば、段の判定は正しいままである。
  - 金額は、読みを入力単価で数える分だけ**過大の側**にずれる。AST がキャッシュの欄を読むまでの安全側である。
  - 残りへ変えると、段も金額も**過小**に倒れ、予算の判定をすり抜ける。
- 逐次は、`message_start` の usage から書き・読みを読む。`message_delta` の書き・読みは、正の値のときだけ置き換える（累計で減らないので、0・欠落は「未報告」として start の値のまま）。
  - 経過報告・最終チャンク・途中終了の計上（IADR-0601）に、同じ値を通す。

### 決定 4 — 計器 `llm.tokens.total` は単価の区分で型を分ける（重ねない）

- `llm.token_type` の値は次の 4 つにする。
  - `input`: キャッシュに関わらない残り
  - `cache_write`
  - `cache_read`
  - `output`
- 4 つは重ならず、和がプロンプト全体＋出力になる。
- 型の軸は、もともと「単価が違うので分ける」ためのものだった（`LlmUsageMetrics` の注記）。
  - キャッシュの書き・読みは単価が違うので、同じ軸に並べる。
  - 「全体」を `input` に残して書き・読みを「うち」の系列にすると、型の間で足す式が二重に数える。
- `input` の意味は、キャッシュを使う呼び出しでだけ「残り」へ変わる（使わない呼び出しでは全体と同じ）。
  - 平均入力を読む式（出力トークン実測の Runbook §3-3）は、3 つの型の和へ直した。
- キャッシュの読みの比率は `cache_read / (input + cache_write + cache_read)` で読む（観測の仕様書に式を置いた）。

### 決定 5 — 単価表に書き・読みの単価を持ち、段の判定量をプロンプト全体にする

- `ModelPriceEntry` と `LongPromptPrice` に、任意の `CacheWritePerMillionTokens`（5 分）/ `CacheReadPerMillionTokens` を足す。
- 金額 = 残り × 入力 ＋ 書き × 書き単価 ＋ 読み × 読み単価 ＋ 出力 × 出力。
- 2 段の判定量はプロンプト全体（提供元と同じ）。境界ちょうどは下段のまま。
- 書き・読みのトークンがあるのに、その段の単価が無いとき:
  - 入力単価で代わりに数えない（書きは過小、読みは 10〜20 倍の過大になる）。
  - 0 円にもしない。`PricingStatus.NoCacheRate` として単価なしに数える。
  - 計器の `llm.pricing_status` は既存の `no_entry`（必要な単価が未登録）にする。値域は 2 値のまま。
- 検証器は次を起動時に落とす。
  - 負値。
  - 親にある書き・読みの単価が上段に無いこと（上段の要求でキャッシュを使ったときだけ単価なしになる）。
- 実配備の値（百万トークンあたり・提供元の単価表 2026-11-05）:

  | モデル | 書き（5 分） | 読み |
  |---|---|---|
  | opus-5-5 | $5 | $0.20 |
  | sonnet-5-5 | $2.50 | $0.10 |
  | haiku-5-5（≦100K） | $0.125 | $0.01 |
  | haiku-5-5（>100K） | $0.625 | $0.05 |

  - claude-api スキルの `model-migration.md` は Sonnet 5.5 の読みを $0.20 と書いていた。
  - 提供元の単価表（$0.10・脚注 0.05 倍 × $2）を採った。

### 決定 6 — AST の呼び手の変更は AST の issue にする

- 本 PR は MSP の側だけを変える（契約の追加・送り方・計上）。
- AST の取引判断（スクリーニング・本判断）の変更は、AST の別 issue（AST#1523）に記録した。
  - 接頭辞を分けて渡す。
  - 接頭辞に呼び出しごとに変わる値（時刻・現在値・保有）を入れない。
  - AST の費用計上をキャッシュの欄で直す。
  - AST の proto の写しへ追随する。
- AST が追随するまで、ゲートウェイの挙動・計上は従前と同じである（接頭辞が来ないので）。

## 採らなかった案

- **接頭辞を `system` へ送る。**
  - 当初の案（issue は「system または先頭のブロック」）。キャッシュの効き方は同じである。
  - しかし、指示の置き場が user から operator へ変わり、判断の内容が変わり得る。
  - 「判断の内容を変えない」の条件を満たす根拠を示せないので採らない。
- **ゲートウェイがプロンプトを自動で切る（固定の見出しで分ける・前回との共通接頭辞を取る）。**
  - 呼び手のプロンプトの構造をゲートウェイが知ることになる（越境判定以外の中身を読まない作法に反する）。
  - 切り位置の誤りが黙って書きの割増になる。
- **`InputTokens` を提供元の `input_tokens`（残り）に揃える。** 既存の呼び手の段・金額が過小に倒れる（決定 3）。
- **計器で `input` を全体のまま残し、書き・読みを別の計器にする。**
  - 計器が増え、用途別の内訳を PromQL で足し引きすることになる。
  - 単価の区分を同じ軸に並べる方が読みやすい。
- **キャッシュの単価を倍率（1.25・0.05/0.1）でコードに持つ。**
  - 倍率はモデルごとに違い（読みが 0.025〜0.1 倍）、改定もある。
  - ADR-0044 決定 3 は単価を有効期間つきの設定で持つと定める。

## 結果

- 接頭辞を分けた要求で、`cache_control` 付きの要求が送られる（`ClaudeProviderPromptCacheTests` が一括・逐次で固定）。
- 接頭辞の無い要求の形は従前どおりで、要求本文に `cache_control` は現れない（同上）。LlmGateway の試験 560 件（新しい試験を含む）が緑。
- 書き・読みを含む応答で、金額が提供元の単価どおりになる（`PromptCachePricingTests`・`DeployedPriceTableTests`・`LlmUsageMetricsTests`）。
- REST・gRPC（一括・逐次）で、接頭辞が届き、書き・読みが返り、計器が型を分けて積む（`CompletionPromptCacheEndpointTests`）。
- 赤の確認: 実装前に、上の新しい試験 26 件が赤（作業仕様書の §受け入れ基準）。

## 残余リスク

- 接頭辞の中身が呼び出しごとに 1 バイトでも違うと、読まれずに書きの割増（1.25 倍）だけが掛かる。
  - 検知: `llm_tokens_total{llm_token_type="cache_write"}` が呼び出しごとに伸び、`cache_read` が 0 のままになる。
- 接頭辞が 512 トークンに満たないと、黙ってキャッシュされない（`cache_write` も `cache_read` も 0）。実測は AST の PoC で読む。
- 5 分を超える間隔（場の開始前・休場明け）では、最初の 1 回が書きになる。1 日 1 回の書きは割増 0.25 倍分で、読み 1 回で回収できる。
- 1 時間の TTL を使い始めるときは、書きの単価を TTL ごとに分ける必要がある。
  - 応答の `usage.cache_creation.ephemeral_1h_input_tokens` を読む。今は 5 分しか送らないので持たない。
