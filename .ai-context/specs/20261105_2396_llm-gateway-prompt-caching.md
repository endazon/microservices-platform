---
title: 作業仕様書 — LLM ゲートウェイでプロンプトキャッシュを使えるようにし、キャッシュの読み・書きを提供元の単価で計上する（#2396）
type: spec
status: done
related_ids: [FR-11, FR-10, NFR, ADR-0010, ADR-0022, ADR-0044, IADR-0531, IADR-0562, IADR-0601, IADR-0671]
author: claude
created: 2026-11-05
updated: 2026-11-05
issue: "#2396"
---

# 作業仕様書 — LLM ゲートウェイのプロンプトキャッシュ（#2396）

> 本仕様書は、実装に着手する前に作成した（着手 2026-11-05。develop `origin/develop` 先端）。

## 事象（issue の記録）

- `ClaudeProvider.BuildParams` は `Messages = [user: request.Prompt]` の 1 件だけを送る。`system` も `cache_control` も無い。
- AST の取引判断（スクリーニング 318 回・本判断 49 回／場）は「固定の前文 → 当日の日報方針 → 監視銘柄 → 銘柄固有部」の順で組まれ、当日のうちは前半が変わらない。呼び出しの間隔は 5 分より短い。
- 費用の計上（`ModelPriceTable.Estimate`）は `usage.input_tokens` だけを入力の量として読む。キャッシュを使い始めると、
  - `input_tokens` はキャッシュの後ろの残りだけになり、キャッシュの書き（1.25 倍）・読み（0.05〜0.1 倍）は計上から消える（**過小**）。
  - Haiku 5.5 の 2 段の判定量（提供元はキャッシュの読み・書きを含む合計で判定する）が `input_tokens` とずれる（IADR-0531 決定 2・残余リスクが予告）。

## 一次情報の確認（2026-11-05。記憶で書かない）

claude-api スキル（`shared/prompt-caching.md`・`csharp/claude-api/README.md`・`shared/models.md`）と、提供元の公開文書を直接読んだ。

| 項目 | 確認した内容 | 出典 |
|---|---|---|
| 置き場 | `cache_control` は tools・`system` のブロック・`messages[].content` のブロック（text 等）に置ける。描画順は tools → system → messages。区切りは最大 4 個 | prompt-caching（docs）・スキル §API reference |
| 最小長 | Opus 5.5・Sonnet 5.5・Haiku 5.5 は **512 トークン**。短い接頭辞は**エラーにならず**キャッシュされない（`cache_creation_input_tokens: 0`） | 同上 |
| TTL | 既定 5 分（`{"type":"ephemeral"}`）。`"ttl":"1h"` で 1 時間。読みで寿命が延びる（追加費用なし） | 同上 |
| 応答の欄 | `cache_creation_input_tokens`（書き）・`cache_read_input_tokens`（読み）・`input_tokens`（**最後の区切りの後ろの、キャッシュに関わらない残りだけ**）。合計 = 3 つの和 | 同上 |
| 単価（USD/MTok） | Opus 5.5: 入力 4・5 分書き 5・読み 0.20／Sonnet 5.5: 2・2.50・**0.10**／Haiku 5.5（≦100K）: 0.10・0.125・0.01、（>100K）: 0.50・0.625・0.05 | pricing（docs） |
| Haiku の段の判定量 | 「プロンプト長は、キャッシュの読み・書きを含むすべての入力トークンを数える」。要求ごとに決まる | pricing §Long context pricing |
| C# SDK | `TextBlockParam { Text, CacheControl = new CacheControlEphemeral() }`。`Usage.CacheCreationInputTokens` / `CacheReadInputTokens` | スキル csharp README |

- 🔴 **スキルの記述と提供元の表が食い違った箇所**: スキルの `model-migration.md` は Sonnet 5.5 の読みを $0.20 と書く。提供元の単価表と脚注（0.05 倍 × $2）は **$0.10** である。**提供元の表を採る**（規則 10: 他人の数えを検証せず転記しない）。

## 方針（IADR-0671）

1. **契約**: 要求に任意の `CacheablePrefix`（REST `cacheablePrefix`・proto `cacheable_prefix = 6`）を足す。
   - モデルが読む全文は `CacheablePrefix + Prompt`（区切り文字を足さない。呼び手は今の全文を 2 つに切るだけ）。
   - 未指定・空文字は今と同じ形（user の 1 件・文字列の content）で送る（後方互換）。
2. **送り方（ClaudeProvider）**: 接頭辞があるときだけ、user の 1 件の content を text ブロック 2 個にする。
   - 1 個目 = 接頭辞（`cache_control: {type: "ephemeral"}`＝5 分）、2 個目 = 残り（空なら送らない）。
   - `system` へは移さない（指示の役割が user から operator へ変わり、判断の内容が変わり得る）。モデル・effort・max_tokens・messages の件数は変えない。
   - TTL は 5 分だけ（呼び出しの間隔が 5 分より短い。1 時間の書きは 2 倍）。最小長に満たない接頭辞はゲートウェイで弾かない（提供元が黙ってキャッシュしないだけで、費用は増えない）。
   - 他のプロバイダ（SelfHosted・Copilot）は全文（`CacheablePrefix + Prompt`）を今と同じ 1 文字列で送る。
3. **数え方（ゲートウェイの中と応答）**: `InputTokens` は**プロンプト全体**（`input + cache_creation + cache_read`）とし、意味を今と変えない。
   - 応答に「うち書き」「うち読み」（`CacheCreationInputTokens` / `CacheReadInputTokens`。proto 11・12、逐次 12・13）を足す。
   - 今の呼び手（AST の費用計上）は `InputTokens × 入力単価` と `InputTokens` による段の選択をしている。全体の意味を保てば、段の判定は正しいままで、費用は**過大の側**（読みを入力単価で数える）にずれるだけになる。残りだけにすると段も金額も**過小**に倒れる。
4. **計上（ゲートウェイの計器）**: `llm.tokens.total` の `llm.token_type` を単価の区分で分ける —— `input`（キャッシュに関わらない残り＝入力単価）・`cache_write`・`cache_read`・`output`。和がプロンプト全体＋出力になる（重ねて数えない）。
5. **単価**: `ModelPriceEntry` と `LongPromptPrice` に任意の `CacheWritePerMillionTokens` / `CacheReadPerMillionTokens` を足す。
   - 金額 = 残り × 入力単価 ＋ 書き × 書き単価 ＋ 読み × 読み単価 ＋ 出力 × 出力単価。
   - 2 段の判定量は**プロンプト全体**（提供元と同じ）。境界ちょうどは下段のまま。
   - キャッシュのトークンがあるのに該当の単価が無いときは、**0 円にせず**単価なし（`PricingStatus.NoCacheRate`。計器の属性は既存の `no_entry`）として数え、警告を出す。
   - 検証器: 負値を落とす。親にキャッシュの単価があるのに上段に無いときも落とす（上段の要求だけが単価なしになる）。
   - 実配備の `appsettings.json` に 3 モデルの書き・読みの単価を入れる。
6. **逐次**: `message_start` の usage の書き・読みを読み、`message_delta` に値があれば累計として置き換える。経過報告・最終チャンク・途中終了の計上（IADR-0601）に同じ値を通す。
7. **AST 側**: 呼び手が接頭辞を分けて渡す変更は AST の別 issue にした（AST#1523。本 PR は MSP 側だけ）。

## 受け入れ基準と試験

| AC | 内容 | 試験（赤を先に確かめる） |
|---|---|---|
| AC1 | 接頭辞を分けた要求は、user 1 件の content が [接頭辞（`cache_control: ephemeral`・ttl なし）, 残り] になる（一括・逐次） | `ClaudeProviderPromptCacheTests` |
| AC2 | 接頭辞の無い要求は今と同じ形（content が文字列・`cache_control` が無い） | 同上 |
| AC3 | model・max_tokens・output_config（effort）・messages の件数は接頭辞の有無で変わらない | 同上 |
| AC4 | 応答の書き・読みが `CompletionResult` / チャンクへ載り、`InputTokens` は 3 つの和になる | 同上 |
| AC5 | 金額が提供元の単価どおり（3 モデル・Haiku の 2 段の境界をキャッシュ込みの合計で判定） | `ModelPriceTableTests`・`DeployedPriceTableTests` |
| AC6 | キャッシュの単価が無いのに書き・読みがある要求は 0 円にせず単価なし | `ModelPriceTableTests`・`LlmUsageMetricsTests` |
| AC7 | `llm.tokens.total` が input（残り）・cache_write・cache_read・output に分かれ、和がプロンプト全体＋出力 | `LlmUsageMetricsTests` |
| AC8 | REST・gRPC の要求の接頭辞がプロバイダへ届き、応答の書き・読みが呼び手へ返る（gRPC の写像の往復を含む） | `CompletionPromptCacheEndpointTests`・`GrpcCompleteTests` |
| AC9 | 接頭辞の無い既存の呼び手の挙動・計上は変わらない | 既存の試験一式が緑のまま |
| AC10 | 検証器が負値・上段の欠けを落とす | `ModelPricingOptionsValidatorTests` |
| AC11 | キャッシュの区切りを持たないプロバイダは全文を 1 つの文字列で送る | `OpenAiProviderStopReasonTests` |

- 赤の確認（実装前・新しい試験の母集合）: 26 件が赤・51 件が緑（77 件。緑は既存の試験と、写像を先に入れた分）。実装後は 77 件とも緑、LlmGateway の全 560 件が緑。

## 母集合（規則 9・10。着手前に引いた）

- 誤りの側の文字列 `usage.input_tokens`・`プロンプトキャッシュを使` で全文書を走査した。
  - 是正する: `ModelPriceTable.cs`（判定量のコメント）・`ModelPricingOptions.cs`・`IADR-0531`（決定 2・残余リスクへ日付つき追記）・`docs/observability/llm-usage-and-cost-metrics.md`（token_type の値域・単価の欄）。
  - `llm_token_type="input"` を平均入力として読む式: `docs/operations/llm-output-token-measurement-runbook.md` §3-3（3 つの型の和へ直す）。
  - 除外: `.ai-context/specs/` の確定済みの仕様書（凍結記録）。
- 契約の写し: `CompletionDto.cs`・`completion.proto`・`LlmGrpcMapping.cs`・`contract-schema-baseline.json`・`proto-contract-baseline.json`（`--update` で再生成）・`docs/api/openapi.yaml`（`check-openapi-dto-drift.js` が 3 件を指摘。応答の 2 欄は required）・`bff.schemas.ts`（`pnpm run codegen` で再生成）・`docs/api/east-west-grpc.md`・`docs/functional/FR-11_*`。
  - AST の proto の写し（`src/ai-stock-trading/...`）は AST が持つ。追随は AST の issue に書く。
- `llm.token_type` の値を読む側: `deploy/grafana/provisioning/dashboards/llm-usage.json` と `deploy/local/observability/grafana.yaml` のパネル「トークン消費量（用途別・入出力別）」は `sum by (llm_purpose, llm_token_type)` で、新しい値は系列として増えるだけ（式は変えない）。型の間で足す式・警報は無い（走査で確認）。
- 導出値: 試験の期待値はすべて単価から計算し直す（下の表）。

## 期待値の計算（試験に書く値）

- Sonnet 5.5、残り 1,000・書き 2,000・読み 10,000・出力 500: 1000×2 + 2000×2.5 + 10000×0.10 + 500×10 = 2,000 + 5,000 + 1,000 + 5,000 = 13,000 /1M = **$0.013**。
- Haiku 5.5、残り 50,000・読み 60,000（合計 110,000 > 100,000 → 上段）・出力 1,000: 50000×0.50 + 60000×0.05 + 1000×2.50 = 25,000 + 3,000 + 2,500 = 30,500 /1M = **$0.0305**。
  - 残りだけ（50,000）で判定すると下段 = 50000×0.10 + 60000×0.01 + 1000×0.5 = 6,100 /1M（過小。旧実装の形）。
- Opus 5.5、残り 0・書き 1,000,000・出力 0: **$5**。読み 1,000,000: **$0.20**。

## 対象外

- AST の呼び手の変更（別 issue）。1 時間の TTL・自動キャッシュ（トップレベル `cache_control`）・事前の温め（`max_tokens: 0`）。
- ダッシュボードのパネル追加（既存のパネルに系列が増える。キャッシュの読みの比率は PR 本文に PromQL を書く）。
