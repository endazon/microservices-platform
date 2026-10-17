---
title: 作業仕様書 — LLM ゲートウェイの Claude 呼び出しを Anthropic.SDK 4.0.0 から公式 Anthropic パッケージ（12.53.0）へ移す（#1749）
type: spec
status: done
related_ids: [FR-11, FR-04, NFR-19, ADR-0010, ADR-0038, ADR-0128, IADR-0037, IADR-0104, IADR-0114, IADR-0225, IADR-0374, IADR-0528, IADR-0531, IADR-0535, IADR-0562]
author: claude
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0010（LLM ゲートウェイ）
  - planning:projects/microservices-platform/07_adr/ADR-0038（用途別フォールバック・429 の境界）
  - planning:projects/microservices-platform/07_adr/ADR-0128（フォローアップ 3: 公式 SDK への移行）
  - planning:projects/microservices-platform/06_technical/04_ai-rag-stack.md（fixed。公式 `Anthropic` パッケージを指定）
issue: "#1749"
---

# 作業仕様書 — Claude 呼び出しを公式 Anthropic SDK へ移す（#1749）

> 本仕様書は実装着手前に作成した（着手 2026-10-17）。判断の記録は **IADR-0562** に置く。
> 基点は MSP `origin/develop` `867d4178`。方針 (A)「公式パッケージへ移行する」は issue のコメント 6233124160 で確定済み。

## 起点となる計画書（トレーサビリティ）

- 起点 issue: **#1749**（計画 `06_technical/04_ai-rag-stack.md` は公式 `Anthropic` を指定、実装は `Anthropic.SDK` 4.0.0。planning#720 補足 2 の実測）。
- 計画: **FR-11**（LLM 送信先の切替）・**ADR-0010**（LLM ゲートウェイ）・**ADR-0038 決定 4**（429 は再試行でありフォールバックではない。フォールバックは 400 系）・**ADR-0128 フォローアップ 3**。
- 計画への環流は不要（計画と実装を一致させる側の変更）。完了後に ADR-0128 フォローアップ 3 の完了を planning へ伝える（オーケストレーターの判断で）。

## 実測（着手時。issue の要約を検証し直した）

### 公式 SDK の版

- NuGet `Anthropic` の安定版（2026-10-17 時点）: 最新 12.58.0（2026-10-15）。**公開から 14 日以上経った最新は 12.53.0（2026-09-30）**。これを固定する。
- 依存（net8.0 群）: `Microsoft.Extensions.AI.Abstractions` ≥ 10.5.1・`System.Net.ServerSentEvents` ≥ 10.0.1・`System.Text.Json` ≥ 10.0.6。
  いずれも推移依存として解決させる（直接参照しないので `PackageVersion` は足さない。CPM の推移的ピンは既存のまま）。
- ソースは `anthropics/anthropic-sdk-csharp` のタグ `Anthropic-v12.53.0`（`d84902e4`）で読んだ。

### 公式 SDK の挙動（ソースで確認）

| 項目 | 12.53.0 の挙動 | 根拠（ソース） |
| --- | --- | --- |
| 再試行 | `MaxRetries` 既定 2。**429・408・409・5xx・接続失敗・期限切れ**を再試行。`MaxRetries = 0` で無効（API の再試行指示も無視） | `Core/ClientOptions.cs`・`AnthropicClient.Execute` / `ShouldRetry` |
| 期限 | `Timeout` 既定 null。**null のとき要求ごとに `max_tokens` から算出**（非ストリーム 30 秒〜10 分、ストリーム 10 分〜1 時間）。**非ストリームで算出値が 10 分を超えると `ArgumentOutOfRangeException`**（`max_tokens` > 20,000）。明示すればそれだけが使われる | `ClientOptions.TimeoutFromMaxTokens`・`MessageService.Create` |
| 期限の効く範囲 | 1 試行ごとの `CancellationTokenSource`。送信は `ResponseHeadersRead` なので**応答ヘッダの到着まで**。超過は `TaskCanceledException`（内側に `TimeoutException`） | `AnthropicClient.ExecuteOnce`・`HttpClientPassthroughHandler` |
| `HttpClient` | 渡した `HttpClient` の `Timeout`（既定 100 秒）が SDK の期限と競合すると注記がある。SDK 既定の内部クライアントは `InfiniteTimeSpan` | `ClientOptions.HttpClient` の注記 |
| 例外 | 非 2xx は `AnthropicApiException` の派生（400 `BadRequest`・429 `RateLimit`・5xx 等）。**`HttpRequestException` ではない**（`StatusCode` は自前のプロパティ）。送信時の `HttpRequestException` は `AnthropicIOException` に包む | `Exceptions/AnthropicExceptionFactory.cs`・`AnthropicApiException.cs` |
| 未知の content ブロック | 判別子に無い型・`type` の無い要素は**例外にせず `ContentBlock(element)` として保持**（`Value` が null） | `Models/Messages/ContentBlock.cs` の `ContentBlockConverter.Read` の `default` |
| effort | `MessageCreateParams.OutputConfig.Effort`（`low`/`medium`/`high`/`xhigh`/`max`） | `Models/Messages/OutputConfig.cs` |
| 停止理由 | `ApiEnum<string, StopReason>`。`Raw()` で未知値も文字列のまま取れる | `Core/ApiEnum.cs` |
| 環境変数 | `ANTHROPIC_BASE_URL`・`ANTHROPIC_AUTH_TOKEN`・`ANTHROPIC_API_KEY` を**明示しなければ**読む | `ClientOptions.BaseUrl` / `AuthToken` / `ApiKey` |

### 現行（旧 SDK）の挙動（手元で実測）

- 一時の試験（コミットしない）で、`ClaudeProvider` へ 429・500・529・400 を返すスタブを与えた（非ストリーム・ストリームとも）。
  **送信は各 1 回（再試行なし）**。例外は `System.Net.Http.HttpRequestException`（`StatusCode` 付き。429→`TooManyRequests`、529→`529`）。
- 期限は `HttpClient.Timeout`（`Llm:AnthropicTimeoutSeconds`、既定 100 秒。IADR-0528）。

### 現行の依存箇所（`Anthropic.SDK` を型として使う所）

- 本番: `Infrastructure/ExternalServices/ClaudeProvider.cs`（116 行）・`Program.cs`（`using` と登録）。
- 試験: `Tests/Grpc/GrpcKestrelFactory.cs`・`Tests/TestWebApplicationFactory.cs`・`AnthropicClientWiringTests`・`ClaudeProviderRequestShapeTests`・`ClaudeProviderStopReasonTests`・`ClaudeProviderThinkingTests`。
- 旧 SDK の回避策（本番 5 ファイル。`ClaudeEffort.cs` 内の `AnthropicRequestContext` を含む。撤去したのはうち 4 ファイル 422 行と `AnthropicRequestContext`、計器 48 行は残す）:
  `AnthropicHttpClient`（68 行）・`AnthropicRequestShapingHandler`（93 行）・`AnthropicResponseSanitizingHandler`（117 行）・`AnthropicContentBlockSanitizer`（144 行）・`AnthropicContentBlockMetrics`（48 行）＝ 470 行。

## 設計（正は IADR-0562）

1. **パッケージ**: `Anthropic.SDK` 4.0.0 を外し `Anthropic` 12.53.0 を入れる（`Directory.Packages.props`・`LlmGateway.csproj`）。
2. **生成点** `AnthropicClientFactory`（`AnthropicHttpClient` を置き換える）:
   - `ApiKey`＝`Llm:ApiKey`（従前どおり未設定は `placeholder`）。**`BaseUrl` は本番 URL を明示し、`AuthToken` は null を明示する**（環境変数で送信先・資格情報が変わらないように。送信先の統制は FR-11 の本体）。
   - **`MaxRetries = 0`**（現行の実測と同じ 1 回送信。理由は下記リスク 2）。
   - **`Timeout` ＝ `Llm:AnthropicTimeoutSeconds`**（解決規則は IADR-0528 のまま。既定 100 秒・不正値は既定へ倒し warn）。`HttpClient.Timeout` は `InfiniteTimeSpan`（期限の正を 1 か所にする）。
   - 一次ハンドラは従前どおり `HttpClientHandler { AutomaticDecompression = All }`。試験は一次ハンドラだけを差し替えられる。
3. **`ClaudeProvider`**: `Messages.Create` / `CreateStreaming` へ置き換える。effort は `OutputConfig.Effort` で送る（要求本文の後付け整形をやめる）。
   本文は先頭の `text` ブロック、停止理由は `Raw()` の文字列（未知値も透過。IADR-0104）。
4. **例外の写像（リスク 1）**: provider の中で `AnthropicApiException` を `HttpRequestException(message, inner, statusCode)` へ、
   `AnthropicIOException` を `HttpRequestException(message, inner)`（状態なし）へ包み直す。**Domain（`LlmFallbackPolicy`）と計器（`LlmCompletionMetrics`）は変えない**。SDK の型は Infrastructure の外へ出さない。ストリームは列挙子を手で回して `MoveNextAsync` の例外も包む。
5. **捨てた型名の可観測性（IADR-0114 決定 5・IADR-0535）**: 応答の content のうち本文に使わないブロック（`text` 以外）の型名を、
   `ClaudeProvider` が計器 `llm.anthropic.content_blocks_dropped`（名前・属性・3 値は不変）へ数え、IADR-0535 と同じ 3 条件でログの段階を分ける。
   ログの分類名は `ClaudeProvider` に変わる。
6. **削除**: `AnthropicHttpClient`・`AnthropicRequestShapingHandler`・`AnthropicResponseSanitizingHandler`・`AnthropicContentBlockSanitizer`・`AnthropicRequestContext`。
   `AnthropicContentBlockMetrics` は残す（provider が使う）。

## 3 つのリスクと固定する試験（いずれも変異で赤を確かめる）

| # | リスク | 固定する試験（期待） | 変異 |
| --- | --- | --- | --- |
| 1 | 例外の型が `AnthropicApiException` になり、`LlmFallbackPolicy.StatusCodeOf` が null になって 400 系のフォールバックが黙って止まる（ADR-0038 決定 4・IADR-0225）。429 の分類（IADR-0374）も消える | 400/404/413/422 → `StatusCodeOf` が同値で `ShouldFallBack=true`、429 → 429 で `false`、5xx → `false`・接続失敗 → 状態なしの transport。非ストリーム・ストリームの両方。投げる型は `HttpRequestException` | 包み直しを外すと赤 |
| 2 | SDK 既定の再試行（2 回・429/5xx 含む）が、①ADR-0038 決定 4 の「429 は呼び出し側が再試行する」と二重になり、②フォールバック鎖と掛け算になり、③1 呼び出しの最長時間が期限 × 3 ＋待機になって IADR-0528 の内外関係（呼び出し側の期限 < ゲートウェイの期限）を崩す | 429・500・529・接続失敗・期限切れで送信は 1 回 | `MaxRetries` を外す（既定 2）と赤 |
| 3 | 期限の既定が 10 分（`max_tokens` 依存）になり、IADR-0528 の設定が効かなくなる。非ストリームで `max_tokens` > 20,000 は例外になる | 実ホストの `AnthropicClient.Timeout` が設定値（7 秒）・未設定で 100 秒。期限を 1 秒にしてヘッダを返さないスタブで 1 秒前後に `TaskCanceledException`（内側 `TimeoutException`）・送信 1 回。`max_tokens` 64,000 の非ストリームが例外にならない | 期限の写像を外すと赤 |

## 試験の作り直し（テスト仕様書の行）

- **T-24**（未知 content ブロック）: サニタイズの試験を、公式 SDK が未知型・thinking を保持したまま本文を取り出せる試験へ置き換える。
  「サニタイズを外すと例外」の変異は前提ごと消える。代わりに、**本文を取り出す条件（`text` だけ）を外すと赤**になる形で固定する。
- **T-33**（期限の解決）: `AnthropicClientFactory.ResolveTimeout` へ移す（値の規則は不変）。生成した `AnthropicClient.Timeout` に載ること。
- **T-34**（配線）: ハンドラ鎖の試験は対象が消える。実ホストの `AnthropicClient` の `Timeout`・`MaxRetries = 0`・`BaseUrl`・`HttpClient.Timeout = Infinite` を見る。
- **T-38**（整形の純関数）: 対象が消える。行は「撤去」と書き、effort の直列化（`output_config.effort`）は T-37 が固定する。
- **T-40**（DI 合成）: 実ホストの登録のまま、生成点の一次ハンドラだけを記録用へ差し替え、`rerank` × `claude-haiku-5-5` で `output_config.effort=low` が載る。
- **T-41**（ログ段階と計器）: 対象を `ClaudeProvider` へ移す（条件と期待は不変）。
- **T-43（新設）**: 上の 3 リスク。

## 母集合（規則 9・10）

走査語: `Anthropic.SDK`・`AnthropicTimeoutSeconds`・`HttpClient.Timeout`・`ANTHROPIC_`・`AnthropicHttpClient`・`AnthropicRequestShapingHandler`・`AnthropicResponseSanitizingHandler`・`AnthropicContentBlockSanitizer`・`AnthropicRequestContext`・`サニタイズ`（`.git`・`bin`・`obj` を除く全追跡ファイル）。

| 該当 | 対応 |
| --- | --- |
| `src/Directory.Packages.props`・`LlmGateway.csproj` | 差し替え |
| `ClaudeProvider.cs`・`Program.cs`・`ClaudeEffort.cs`（コメント・`AnthropicRequestContext`） | 書き換え |
| `Domain/Routing/LlmFallbackPolicy.cs` のコメント（「Anthropic.SDK 4.0.0 は `HttpRequestException.StatusCode` を設定して投げ」） | 現行の事実（provider が包み直す）へ改める。判定は変えない |
| `Common/Observability/AnthropicContentBlockMetrics.cs` のコメント・計器の説明 | 「SDK 解釈前に除去」を「本文に使わず捨てた」へ改める（計器の名前・属性は不変） |
| 試験 6 ファイル＋`AnthropicHttpClientTests`・`AnthropicContentBlockSanitizerTests`・`AnthropicResponseSanitizingHandlerLogLevelTests`・`TestContentBlockMetrics`・`LlmFallbackPolicyTests`・`CompletionFallbackEndpointTests`（コメント） | 作り直し・削除・コメント是正 |
| `docs/functional/FR-11_llm-egress-routing.md`（effort の節・未知型除去の節・受け入れ基準） | 改める |
| `docs/tests/FR-11_llm-egress-routing.md`（T-24・T-33・T-34・T-38・T-40・T-41・T-43） | 改める |
| `docs/tech/composability-classification.md`（「Anthropic SDK」） | 公式 SDK の名前へ |
| `docs/observability/llm-completion-metrics.md`（計器の説明） | 改める |
| `docs/operations/operations.md` §Anthropic 呼び出しの期限（走査語 `AnthropicTimeoutSeconds`・`HttpClient.Timeout` で追加で引いた。「一括では応答の全文が届くまで」） | 効く範囲（応答ヘッダの到着まで）と再試行しないことを書く |
| `deploy/docker-compose.yml`・`deploy/helm/.../values.yaml` の期限のコメント | 記述（キー名・既定・内外関係）は移行後も正しいので触らない |
| `scripts/test-spec-coverage-baseline.json` | 消えた試験クラスを外し、新しいクラスを足す（`--update`） |
| IADR-0114・0528・0531・0535・0225・0037 の本文 | **書き換えない**。置き換えた決定にだけ日付つき追記を置く（0114・0528・0531・0535・0225）。0037 は SDK の API 名を挙げるだけで決定は不変なので触らない |
| `.ai-context/adr/README.md` の索引 | 0562 の行を足す |
| `.ai-context/specs/`・`.ai-context/superpowers/` | 凍結記録なので触らない |
| `scripts/fixtures/review-floor/pr1918-review1.txt` | 過去のレビューの記録（fixture）なので触らない |

## 受け入れ基準

- [x] `Anthropic.SDK` への参照（`PackageReference`・`using`・型）が本番・試験から無くなり、`Anthropic` 12.53.0 で build・全試験が通る（LlmGateway.Tests 481 件）。
- [x] 3 リスクの試験（T-43）が通り、各変異で赤になる（下表）。
- [x] 既存の T-16〜T-18（停止理由）・T-35〜T-37・T-39〜T-42 が公式 SDK の上で通る。
- [x] 捨てた型名の計器とログ段階（T-41）が provider で成り立つ。
- [x] 回避策 4 ファイルと `AnthropicRequestContext` が消える。
- [x] 文書（機能仕様・テスト仕様・合成可能性の分類・計器の仕様・運用仕様の期限の節）が新しい形を書く。`Anthropic.SDK` の文字列が残るのは経緯の記述（「旧 `Anthropic.SDK` 4.0.0 …」）と凍結記録だけ。
- [x] platform slnx の build・format、`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`（1122 件）、backend-library-baseline の ratchet が緑。

## 変異の結果（2026-10-17 実測。各変異は 1 つずつ入れて LlmGateway.Tests を全件流し、戻した）

| 変異 | 赤になった試験 |
| --- | --- |
| M1: `ClaudeProvider` の包み直しを外す（`IsTranslated` を常に false） | T-43 ① の 18 件（非ストリーム 7・ストリーム 7・計器の軸 3・接続失敗 1） |
| M2: 生成点の `MaxRetries = 0` を外す（SDK 既定 2 回） | T-43 ② の 5 件＋期限の 1 件、T-33 の生成 4 件、T-34 の 1 件（計 10 件） |
| M3: 生成点の `Timeout` の写像を外す（SDK 既定） | T-43 ③ の 1 件（30 秒待って赤）、T-33 の生成 4 件、T-34 の 2 件（計 7 件） |
| M4: 本文を「`text` 欄を持つ最初のブロック」から取る | T-24 の「本文の前の非 text ブロック」の 1 件 |
| M5: `Program.cs` を SDK 既定の `new AnthropicClient(...)` の登録へ戻す | T-34 の 3 件 |
| M6: 要求に `OutputConfig` を載せない | T-37 の 2 件・T-40 の 1 件 |
| M7（［2026-10-17 追記 / PR #1996 のレビュー］）: 一括の呼び出し全体の期限を外す（`Messages.Create` へ呼び出し側の `ct` をそのまま渡す） | T-43 ③ の「ヘッダは返すが本文を返さない相手」の 1 件（xUnit の 15 秒の上限で赤） |

## PoC（配備後に実呼び出しで確かめる項目。市場が閉じた後）

報告と PR 本文に載せる（非ストリーム・ストリーム・thinking・effort・期限・429 の扱い・型名の計器）。
