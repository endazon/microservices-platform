---
title: IADR-0562 LLM ゲートウェイの Claude 呼び出しを Anthropic.SDK 4.0.0 から公式 Anthropic パッケージ（12.53.0）へ移し、SDK の例外は provider で HttpRequestException へ包み直し、再試行は 0、期限は Llm:AnthropicTimeoutSeconds を SDK の Timeout へ写す
type: impl-adr
status: Accepted
related_ids: [FR-11, FR-04, NFR-19, ADR-0010, ADR-0038, ADR-0128, IADR-0037, IADR-0104, IADR-0114, IADR-0225, IADR-0374, IADR-0528, IADR-0531, IADR-0535]
author: claude
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/microservices-platform/06_technical/04_ai-rag-stack.md（fixed。Claude の呼び出しに公式 `Anthropic` パッケージを指定）
  - planning:projects/microservices-platform/07_adr/ADR-0010（LLM ゲートウェイ）
  - planning:projects/microservices-platform/07_adr/ADR-0038（決定 4: 429 は再試行でありフォールバックではない。フォールバックは 400 系）
  - planning:projects/microservices-platform/07_adr/ADR-0128（フォローアップ 3: 公式 SDK への移行）
related_specs:
  - ../specs/20261017_1749_anthropic-official-sdk.md
---

# IADR-0562: Claude 呼び出しを公式 Anthropic SDK へ移す（#1749）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: Accepted
- 日付: 2026-10-17
- 決定者: 方針 (A)「公式パッケージへ移行する」は**オーケストレーター**（#1749 のコメント 6233124160。計画 fixed 文書との一致を根拠とする）。版・再試行・期限・例外の写像・可観測性の置き場所は claude。

## 起点・関連

- 起点 issue: [#1749](https://github.com/endazon/microservices-platform/issues/1749)（計画は公式 `Anthropic`、実装は第三者製 `Anthropic.SDK` 4.0.0。planning#720 補足 2 の実測）。
- 関連する計画書 ID: FR-11・ADR-0010・ADR-0038 決定 4・ADR-0128 フォローアップ 3。計画への環流は不要（実装を計画へ揃える変更）。
- 部分的に置き換える実装判断:
  - [IADR-0114](./IADR-0114_anthropic-unknown-content-block-sanitizing.md)（未知 content ブロックの除去）: 決定 1〜4・6 の**仕組み**（許可リストの委譲ハンドラ）を撤去する。決定 5（捨てた型名を記録する）は残し、置き場所を provider へ移す（本 IADR 決定 5）。
  - [IADR-0528](./IADR-0528_anthropic-http-timeout-config.md)（期限の設定）: 設定キー・既定・不正値の規則（決定 1・2・4）は変えない。**写す先**を `HttpClient.Timeout` から SDK の `ClientOptions.Timeout` へ、生成点を `AnthropicHttpClient` から `AnthropicClientFactory` へ改める（決定 3 の置き換え）。
  - [IADR-0531](./IADR-0531_claude-5-5-model-migration.md)（5.5 系への切替）決定 3: effort の**送り方**（`AnthropicRequestShapingHandler` が要求本文へ足す）を SDK の `OutputConfig.Effort` へ改める。用途別の設定・値域・対応集合は変えない。
  - [IADR-0535](./IADR-0535_anthropic-content-block-drop-log-level.md)（ログ段階と計器）: 3 条件・計器の名前・属性・値域は変えない。**数える場所**を委譲ハンドラから `ClaudeProvider` へ移す。
  - [IADR-0225](./IADR-0225_llm-purpose-fallback-chain-and-429-boundary.md)（フォールバック鎖と 429 の境界）: 判定器は変えない。前提「`Anthropic.SDK` 4.0.0 は `HttpRequestException.StatusCode` を設定して投げる」を「provider が包み直して同じ形にする」へ改める（本 IADR 決定 1）。
- 変えない実装判断: [IADR-0037](./IADR-0037_llm-sse-streaming.md)（真のストリーミング）・[IADR-0104](./IADR-0104_llm-stop-reason-refusal.md)（停止理由と拒否時の本文破棄）・[IADR-0374](./IADR-0374_llm-upstream-status-metric-axis.md)（上流状態の計器の軸）。
- 作業仕様書: [20261017_1749_anthropic-official-sdk](../specs/20261017_1749_anthropic-official-sdk.md)（実測の表・母集合・変異の結果）。

## コンテキストと課題

計画 `06_technical/04_ai-rag-stack.md`（fixed）は公式 `Anthropic` パッケージを指定する。実装は第三者製の `Anthropic.SDK` 4.0.0（26 か月前の版）を使い、
その欠落（thinking 等の content ブロックで応答全体を失う・effort を送れない）を HTTP 層の回避策（本番 4 ファイル 422 行。ほかに計器 `AnthropicContentBlockMetrics` 48 行と `AnthropicRequestContext`）で補っていた。

公式 SDK（12.x）は thinking・未知のブロック・effort・`refusal` を型として扱える。一方、**既定の振る舞いが旧 SDK と 3 点で違い**、
そのまま差し替えると例外もログも出ないまま統制が外れる（作業仕様書の実測表）:

1. 非 2xx の例外が `AnthropicApiException`（`HttpRequestException` ではない）。`LlmFallbackPolicy.StatusCodeOf` が null になり、400 系のフォールバック（ADR-0038 決定 4）も 429 の区別（IADR-0374）も黙って消える。
2. 既定で 2 回再試行し、429・408・409・5xx・接続失敗・期限切れが対象。旧 SDK は再試行しなかった（実測: 429・500・529・400 とも送信 1 回）。
3. 期限の既定は null で、要求ごとに `max_tokens` から算出する（非ストリーム 30 秒〜10 分・ストリーム 10 分〜1 時間）。非ストリームで算出が 10 分を超える（`max_tokens` > 20,000）と例外になる。

## 検討した選択肢

1. **公式 SDK へ移し、既定を変える値を生成点 1 か所で固定し、例外を provider で包み直す（採用）**。
2. **(B) 旧 SDK を使い続け、計画の記述の是正を環流する（棄却）** —— fixed 文書を実質的に変えることになり、それを正当化する技術的理由が無い（#1749 の方針）。
3. **例外の判定器（Domain の `LlmFallbackPolicy`）に `AnthropicApiException` を足す（棄却）** —— Domain が SDK の型を知ることになり、判定器が「全プロバイダを 1 つの形で扱う」前提（IADR-0225）を崩す。計器（`LlmCompletionMetrics.ClassifyUpstreamStatus`）も同じ一次情報を読むので、片方だけ直す事故の余地が増える。
4. **SDK の再試行を残し、フォールバックを外す・減らす（棄却）** —— 下の決定 3 の理由。
5. **旧 SDK 向けの委譲ハンドラを残したまま SDK だけ差し替える（棄却）** —— 要求整形は SDK が effort を送れるので二重になり、サニタイズは公式 SDK では不要（未知のブロックを例外にしない。ソースで確認）。残すと「何を守っているか」が読めない層になる。

## 決定

### 決定 1 — SDK の例外は `ClaudeProvider` の中で `HttpRequestException` へ包み直す。Domain に SDK の型を持ち込まない

- `AnthropicApiException`（非 2xx）→ `HttpRequestException(message, inner, StatusCode)`。`AnthropicIOException`（送信の失敗）→ `HttpRequestException(message, inner)`（状態なし＝ transport）。
  それ以外の SDK 例外（応答の形の異常）は包まずに伝える（状態の取れない失敗＝フォールバックしない。旧 SDK の `JsonException` と同じ扱い）。元の例外は `InnerException` に残す。
- ストリームは `yield` を `try` に置けないので、列挙子を手で回し `MoveNextAsync` だけを包む（要求の送信は最初の `MoveNextAsync` で起きる）。
- `LlmFallbackPolicy`・`LlmCompletionMetrics` は 1 行も変えない（コメントの前提だけを改めた）。
- 固定: T-43 ①（400/404/413/422 は `ShouldFallBack=true`、429・500・529 は `false`、計器の軸は旧 SDK と同値、接続失敗は状態なしの transport。非ストリーム・ストリームとも）。

### 決定 2 — 生成点は `AnthropicClientFactory` の 1 か所。送信先と資格情報も明示する

- `ApiKey`＝`Llm:ApiKey`（未設定は従前どおり `placeholder`）。**`BaseUrl` は本番 URL（`https://api.anthropic.com`）を明示し、`AuthToken` は null を明示する。**
  明示しないと SDK は `ANTHROPIC_BASE_URL`・`ANTHROPIC_AUTH_TOKEN` を読む。送信先が設定の外（環境変数）で変わる経路を作らない（送信先の統制は FR-11 の本体）。
- 一次ハンドラは従前どおり `HttpClientHandler { AutomaticDecompression = All }`（IADR-0114 決定 2 の後半を引き継ぐ）。試験は一次ハンドラだけを差し替え、ほかの設定は本番と同じものを通す。
- 固定: T-34（実ホストの `AnthropicClient` で `MaxRetries=0`・`BaseUrl`・`AuthToken=null`・`HttpClient.Timeout=Infinite`）。

### 決定 3 — 再試行は 0（`MaxRetries = 0`）

- 旧 SDK の実測（送信 1 回）と同じにする。理由:
  - **429 の再試行の形（回数・バックオフ・`Retry-After`）は決めていない**（IADR-0225 §フォローアップ 1。計画 ADR-0038 決定 4 は「429 は再試行である」と述べるだけ）。
    SDK の既定を残すと、決めていない方針（2 回・指数バックオフ・`Retry-After` の尊重）を SDK の版が黙って決めることになる。
    呼び出し側（AST 等）が既に再試行している経路では二重にもなる。
  - **フォールバック鎖と掛け算になる**。400 系は SDK の再試行の対象外だが、鎖の各候補が 5xx・期限切れで 3 回ずつ送られる。
  - **期限の内外関係が崩れる**（IADR-0528 決定 4「呼び出し側の期限はゲートウェイの期限より短く保つ」）。再試行すると 1 呼び出しの最長が期限 × 3 ＋待機（最大 8 秒×2 または `Retry-After`）になり、設定値が上限を表さなくなる。
- 再試行を入れたくなったら、呼び出し側との分担と期限の関係を決めてから別 IADR で入れる。
- 固定: T-43 ②（429・500・529・接続失敗・期限切れで送信 1 回。非ストリーム・ストリームとも）。

### 決定 4 — 期限は `Llm:AnthropicTimeoutSeconds` を SDK の `ClientOptions.Timeout` へ写す。`HttpClient.Timeout` は無限

- 解決規則（未設定・空白は 100 秒、1〜2,147,483 の整数は採る、不正は 100 秒へ倒して warn 1 行）は IADR-0528 決定 1・2 のまま。上限の根拠は「`HttpClient.Timeout` の上限」から「SDK が 1 試行ごとに作る `CancellationTokenSource` の上限」（同じ `int.MaxValue` ミリ秒）へ読み替える。
- **常に明示する**ので、SDK の `max_tokens` からの算出（既定 10 分）は使われず、非ストリームで `max_tokens` > 20,000 の例外にも当たらない。
- `HttpClient.Timeout` は `InfiniteTimeSpan`。SDK の注記どおり、両方に値があると競合する。期限の正は SDK の 1 か所にする。
- SDK の `Timeout` が効くのは 1 試行の**応答ヘッダの到着まで**である（SDK は `ResponseHeadersRead` で送る）。
- **一括は、`ClaudeProvider.CompleteAsync` が呼び出し全体に同じ期限の取り消し（`CancelAfter` した linked token）を掛け、全文の受信までを期限に入れる**
  （PR #1996 の AI レビューの 🟡 で追加。旧 SDK の `HttpClient.Timeout` も本文の受信完了まで効いていた）。ヘッダの後に本文の受信が止まっても期限で切れる。
- 超過は `TaskCanceledException`（内側 `TimeoutException`）で、旧 SDK の `HttpClient.Timeout` 超過と同じ型である（`CompletionUseCase` の扱いは変わらない）。
  呼び出し側の取り消しは包まずに伝える（`TimeoutException` を内側に持たない）ので、期限切れと区別できる。
- **逐次には呼び出し全体の期限を掛けない。** 期限が効くのは応答ヘッダの到着までで、その後の受信は呼び出し側の取り消し（gRPC の deadline・切断）だけが止める。
  長い出力が長く流れ続けるのは正常であり、全体に期限を掛けると正当な長い応答を途中で切る。旧 SDK も同じ範囲であり（IADR-0528・運用仕様書）、移行で変わっていない。
  受信が途中で止まったまま呼び出し側も取り消さない場合に待ち続ける点は、無通信の期限（idle timeout）を決めるまで残る（残余リスク）。
- 固定: T-33（解決規則・生成した `AnthropicClient.Timeout`）・T-34（実ホスト 7 秒・未設定 100 秒）・T-43 ③（期限 1 秒でヘッダを返さない相手・**ヘッダは返すが本文を返さない相手**へ送ると約 1 秒で `TaskCanceledException`（内側 `TimeoutException`）・送信 1 回／呼び出し側の取り消しは `TimeoutException` を持たない／`max_tokens` 64,000 の非ストリームが例外にならない）。

### 決定 5 — 本文に使わない content ブロックの型名は `ClaudeProvider` が数え、IADR-0535 の 3 条件でログを出す

- 公式 SDK は未知の型も `ContentBlock(element)` として保持する。provider は本文を**先頭の `text` ブロック**から取り（旧 SDK の実装と同じ）、`text` 以外のブロックの型名（`type` の無い要素は `(型不明)`）を計器
  `llm.anthropic.content_blocks_dropped` へ数える。計器の名前・属性 `llm.content_block_type`・3 値（`thinking` / `redacted_thinking` / `other`）は変えない（ダッシュボードの継続）。
- ログの 3 条件（IADR-0535 決定 1）の「既知ブロックが 1 個以上残る」は「本文の `text` ブロックが 1 個以上ある」と読む。ログは型名だけで本文を載せない（IADR-0114 決定 5 の機微の線）。分類名は `ClaudeProvider` になる。
- `tool_use` は旧来の許可リスト（「SDK が解釈できる型」）に入っていたため数えなかったが、ゲートウェイはツールを送らないので**想定外**として Warning に入る。
- ストリームは従前どおり数えない（旧 SDK でもサニタイズは SSE を触らなかった）。
- 固定: T-41（`ClaudeProviderContentBlockLogTests`）・T-24（`ClaudeProviderThinkingTests`。本文の前の非 text ブロックの文字列が本文へ混ざらない）。

### 決定 6 — 版は 12.53.0。推移依存は足さない

- 公開から 14 日以上経った最新（2026-09-30 公開。2026-10-17 時点の最新は 12.58.0）。依存（`Microsoft.Extensions.AI.Abstractions`・`System.Net.ServerSentEvents`・`System.Text.Json`）は直接参照しないので `PackageVersion` を足さない（CPM の推移的ピンは、脆弱性の是正が要るときに足す既存の運用のまま）。
- 撤去: `AnthropicHttpClient`・`AnthropicRequestShapingHandler`・`AnthropicResponseSanitizingHandler`・`AnthropicContentBlockSanitizer`・`AnthropicRequestContext`（本番 4 ファイルと 1 型）。

## 理由

- 計画（fixed）との一致。回避策が守っていた性質（未知型で応答を失わない・effort を送る）は SDK が型として持つので、守る層を消して読みやすくする。
- 既定の違い 3 点は、どれも**例外もログも出さずに統制を外す**種類である。生成点と provider の 2 か所で固定し、変異で赤になる試験を置く。

## 結果

- 本番の回避策 4 ファイル（422 行）と `AnthropicRequestContext` が消えた（計器 `AnthropicContentBlockMetrics` は残る）。`ClaudeProvider` は SDK の型を外へ出さない。
- 試験: T-24・T-33・T-34・T-40・T-41 を作り直し、T-38 は対象ごと撤去、T-43 を新設した。変異（包み直し・再試行 0・期限・一括の呼び出し全体の期限・本文の条件・effort・Program.cs の登録）はいずれも赤になる（作業仕様書）。
- 稼働中環境への操作なし。配備は市場が閉じた後に行い、実呼び出しで確かめる（作業仕様書・PR の PoC 手順）。

## 残余リスク

- 一括の本文の受信は、決定 4 の呼び出し全体の期限で旧 SDK と同じく期限に入る（当初の版では期限の外に出ていた。PR #1996 のレビューで是正）。
  PoC では長い出力（`max_tokens` 8192 規模）の所要時間を測り、期限 100 秒との関係が従前と同じであることを確認項目として残す。
- 逐次は、ヘッダの後に受信が止まり呼び出し側も取り消さないと待ち続ける（旧 SDK と同じ。決定 4）。
- SDK の版を上げると既定や例外の型が変わり得る。T-43 は生成点越しに振る舞いを見るので、上げたときに赤で気付ける。
- `Translate` が包まない SDK 例外（`AnthropicSseException`・`AnthropicInvalidDataException`）は計器では `other` になる（旧 SDK の `JsonException` と同じ）。
