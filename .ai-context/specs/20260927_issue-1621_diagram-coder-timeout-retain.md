---
title: REST の図のコード化が LLM ゲートウェイの時間切れで正規化全体を失敗させる件を直し、図を画像として残す（#1621）
type: spec
status: done
related_ids: [FR-12, UC-06, FR-11, ADR-0010, ADR-0012, ADR-0027, ADR-0029, IADR-0008, IADR-0400]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/03_usecases/01_usecases.md UC-06 例外フロー（図コード化（LLM）の失敗は画像保持へ縮退し、後日の人手補正・再登録でコード化する）
  - planning:projects/microservices-platform/07_adr/ADR-0012（変換パイプライン・段階的コード化）
related_specs:
  - 20260927_issue-1608_purger-timeout-isolation.md
  - 20260926_issue-1604_refresher-and-sync-loop-timeouts.md
issue: "#1621"
---

# 仕様書: REST の図のコード化の時間切れを画像保持へ畳む（#1621）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`）を一次情報とする。

## 起点となる計画書（トレーサビリティ）

- ユースケース: **UC-06**（文書を正規化変換する）例外フロー「本文変換・資産保存の恒久失敗は再試行し、継続失敗はデッドレターへ送る。
  **図コード化（LLM）の失敗は画像保持へ縮退し**、後日の人手補正・再登録でコード化する」（隣接クローン `../project-planning` の
  `projects/microservices-platform/03_usecases/01_usecases.md` で 2026-09-27 に確認）
- 機能要求: **FR-12**（原本の正規化変換）、FR-11（変換時の LLM 呼び出しの送信制御）
- 関連 ADR: ADR-0012（変換パイプライン・段階的コード化）、ADR-0010（LLM ゲートウェイ）、ADR-0029（gRPC の期限）
- 関連 IADR: **IADR-0008 決定 B-2**（コード化不能・送信拒否・呼び出し失敗はすべて画像保持へ収束）、IADR-0400 決定 5（gRPC 実装の輸送失敗も同じ理由で画像保持）
- 起点 issue: #1621（#1608 の作業仕様書「射程外の発見」で見つかった同じ型の取りこぼし）

## 事実（着手前の実測）

- `LlmGatewayDiagramCoder.CodeAsync`（ConversionService の REST 実装。`Services:LlmGatewayGrpc` 未構成時の既定）の捕捉は
  `catch (Exception ex) when (ex is not OperationCanceledException)`。
- `HttpClient.Timeout`（名前付きクライアント。明示の設定なし＝既定 100 秒）の経過は `TaskCanceledException`（`OperationCanceledException` の派生・
  内側に `TimeoutException`）で表れる → 捕捉を抜け、`NormalizationService` → `RawDocumentFetchedConsumer.Handle` の
  `catch (Exception)` でジョブが `failed` になり再送出される（Wolverine の再試行 → 使い切ればデッドレター）。
- `ct` の出所: `RawDocumentFetchedConsumer.Handle(ev, envelope, ct)` → `normalizer.NormalizeAsync(ev, ct)` → `diagramCoder.CodeAsync(figure, confidentiality, ct)` →
  `http.PostAsJsonAsync(…, ct)` / `ReadFromJsonAsync(ct)`。ConversionService の中では差し替え・連結は無い。`IDiagramCoder` の利用者は
  `NormalizationService` だけ（`git grep -n "IDiagramCoder\|diagramCoder" -- src/knowledge/backend/Services/ConversionService/Features`）。
- 🔴 **ただし受け口の `ct` そのものが、停止要求と Wolverine の 1 通ごとの実行期限の連結である**（本仕様書の最初の版はここを「呼び出し元（メッセージ消費）の ct」
  とだけ書き、実行期限を見落としていた。監査が検出）。WolverineFx 6.24.4 は `HandlerChain.ExecutionTimeoutInSeconds`、未設定なら
  `WolverineOptions.DefaultExecutionTimeout`（**60 秒**。`DiagramCodingLimitsTests.Wolverine_の既定の実行期限は60秒である` が固定）の CTS を受け口の ct へ連結する
  （方針で 1 秒にすると受け口の ct が実際に約 1 秒で立つことを `受け口の期限の方針は受け口の_ct_をその長さで取り消す` がローカルキューで実測）。
  本リポジトリは実行期限をどこでも設定していなかった（`git grep -n "ExecutionTimeout\|MessageTimeout" -- src` を origin/develop（`fd474424`）に対して引くと試験以外 0 行。作業木では本 PR が足した 4 行〔`DiagramCodingLimits.cs` 2・`RawDocumentFetchedTimeoutPolicy.cs` 2〕が出る・`UsePlatformMessagingDefaults` も設定しない）。
  図のコード化の期限は REST が 100 秒、gRPC が期限なしで、**どちらも 60 秒より長い** → 応答しないゲートウェイは**いつも受け口の ct が先に立つ形で**終わり、
  `!ct.IsCancellationRequested` の縮退の枝には本番では届かない。

## 受け入れ基準

| # | 基準 | 写像先 |
| --- | --- | --- |
| AC-1 | REST 実装は、自分の期限の時間切れ（`TaskCanceledException`・受け口の ct は立っていない）を `Retain("llm-call-failed")` へ畳む | `LlmGatewayDiagramCoderTests.Retains_when_gateway_times_out`（FR-12 テスト仕様 T-44） |
| AC-2 | 対照: 受け口の ct による取り消し（その ct を運ぶ `TaskCanceledException`）は畳まずに外へ出す | `LlmGatewayDiagramCoderTests.Propagates_caller_cancellation`（T-44） |
| AC-3 | AC-1 の器が本物の時間切れの形（`TaskCanceledException`・内側 `TimeoutException`・ct 未取り消し）を作ること | `LlmGatewayDiagramCoderTests.Hanging_gateway_fixture_produces_the_timeout_shape`（T-44） |
| AC-4 | **受け口の期限つき ct の下で**（縮尺: 1 回 1 秒・総枠 2 秒・受け口 4 秒）、応答しないゲートウェイでも図は画像として残り、ジョブは受け口の期限が立つより前に `succeeded` | `DiagramCodingTimeoutPipelineTests.Hung_gateway_keeps_the_figure_as_an_image_before_the_handler_timeout_fires`（UC-06 テスト仕様 T-46） |
| AC-5 | 受け口から端まで（対照）: 受け口の ct の取り消しは外へ出て、図を画像として保管せず発行もしない | `DiagramCodingTimeoutPipelineTests.Caller_cancellation_propagates_out_of_the_consumer`（T-47） |
| AC-6 | gRPC 実装の絞りは元から同じ境界（直さない）。呼び出し元に由来しない `RpcException(DeadlineExceeded / Cancelled)` は画像保持、受け口の ct の取り消しは `RpcException(Cancelled)` のまま外へ出る。ct が生成クライアントへ渡る | `LlmGatewayGrpcDiagramCoderTests.呼び出し元に由来しない期限切れと取り消しは画像保持へ縮退する`・`呼び出し元の取り消しは畳まずに外へ出す`（T-45） |
| AC-7 | **1 回の呼び出しの期限**を構成から与える（`Conversion:DiagramCodingTimeoutSeconds`・既定 20 秒・下限 1 秒）。REST は `HttpClient.Timeout`、gRPC は `Deadline` に同じ値 | `LlmGatewayGrpcDiagramCoderTests.呼び出しごとに構成の期限を付ける`（T-45）、`DiagramCodingLimitsTests.本番の配線は三つの上限を既定値で張る`（T-49）、AC-4 の試験（本番と同じ登録を通す） |
| AC-8 | **1 文書の総枠**（`Conversion:DiagramCodingBudgetSeconds`・既定 120 秒）を使い切ったら、残りの図はゲートウェイを呼ばずに画像として残し、ジョブは続く | `NormalizationServiceTests.Retains_remaining_figures_without_calling_the_coder_once_the_budget_is_exhausted`・`Calls_the_coder_for_every_figure_while_the_budget_lasts`、`DiagramCodingTimeoutPipelineTests.Exhausted_budget_retains_the_remaining_figures_without_calling_the_gateway`（T-48） |
| AC-9 | **受け口の実行期限**（`Conversion:HandlerTimeoutSeconds`・既定 300 秒）を `RawDocumentFetched` のハンドラへ与え、「受け口 ＞ 総枠 ＋ 1 回」でなければ起動を止める | `DiagramCodingLimitsTests`（既定値・下限・順序の検査・方針が受け口の ct を取り消す・Program.cs の配線。T-49） |
| AC-10 | IADR-0008 決定 B-2 へ日付つき追記（本文は書き換えない。数値と選択の理由を含む）。新しい IADR 番号は取らない | 同 IADR の追記 |
| AC-11 | 他の Wolverine の受け口で、既定 100 秒または期限なしの外向き呼び出しに頼るものを洗い出し、判定を記す（本 PR では直さない） | 下の「母集合」軸 4 |

## 設計

- 捕捉を `when (ex is not OperationCanceledException || !ct.IsCancellationRequested)` にする（#1607＝#1604、#1619＝#1608 と同じ形）。
  理由文字列は従前の `llm-call-failed` のまま（gRPC 実装と同じ。運用の集計を輸送で割らない）。
- gRPC 実装の捕捉は `when (ex is RpcException or InvalidOperationException && !ct.IsCancellationRequested)`（`is` のパターン結合子 `or` は `&&` より強く結合）。
  チャネルは `ThrowOperationCanceledOnCancellation` を立てておらず、期限切れ・取り消しは `RpcException(DeadlineExceeded / Cancelled)` で届く → 絞りは既に正しい。
- **3 つの時間の上限**（`Infrastructure/Configuration/DiagramCodingLimits.cs`。内側から 1 回 ＜ 総枠 ＜ 受け口）:
  - 1 回の期限 20 秒: REST は `DiagramCoderRegistration.AddRestDiagramCoder` が名前付きクライアントの `Timeout` に、gRPC は `LlmGatewayGrpcDiagramCoder` が
    呼び出しの `Deadline`（`TimeProvider` の現在時刻 ＋ 期限）に与える。登録を Program.cs から切り出したのは、端から端の試験が**本番と同じ登録**で期限を測るため
    （試験側で `HttpClient` を組むと本番の `Timeout` を外しても緑のまま）。
  - 総枠 120 秒: `NormalizationService` が `TimeProvider.GetTimestamp()` で図のコード化の開始からの経過を測り、各図の**呼び出し前**に使い切りを判定する。
    使い切りなら `Retain("coding-budget-exhausted")` として既存の画像保持の枝へ流す（図の記録の形・SC-07 の表示は変えない）。
  - 受け口 300 秒: `RawDocumentFetchedTimeoutPolicy`（Wolverine の `IHandlerPolicy`）が `RawDocumentFetched` のチェーンにだけ `ExecutionTimeoutInSeconds` を与える。
  - 数値の根拠: 受け口の既定 60 秒のままでは総枠 ＋ 1 回（140 秒）が収まらない。総枠を 60 秒未満へ縮めると 1 回 20 秒で 2 図しか試せない。
    300 秒なら本文変換・保管に 160 秒残る。副作用: 固まった pandoc が 1 回の試行を占有する時間が 60 → 300 秒に延びる（再試行の間隔・回数は不変）。
- 試験の器:
  - 応答を返さず要求の ct が立つまで待つ `HttpMessageHandler`（届いた要求を数える）。ネットワークは使わない（ハンドラ直結。待ち受けも無い）。
  - 端から端の試験は受け口へ **`DiagramCodingLimits.HandlerTimeout` で CancelAfter した ct**（Wolverine が渡す ct の縮尺版）を渡す。縮尺は構成の下限（1 秒）に合わせ
    1 回 1 秒・総枠 2 秒・受け口 4 秒。
  - 総枠の単体試験は試験だけが進める時計（`ManualTimeProvider`）で経過を作る（実時間に依存しない）。

## 母集合（同じ欠陥の走査）

走査は 2026-09-27、`origin/develop` = `3b9036e2` の上で行った。対象は `src/platform/**` と `src/knowledge/**` の試験以外
（`/Tests/` と `.Tests/` をパスで除外。`src/ai-stock-trading` は別リポジトリの submodule のため**除外**）。問いは「**LLM 呼び出しの失敗を縮退へ畳む
（と名乗る）処理のうち、型だけの絞りで時間切れを素通しするものが他にあるか**」である。

- 軸 1（型だけの絞り）: `git grep -n "is not OperationCanceledException" -- 'src/platform/**' 'src/knowledge/**'` から試験を除き、`IsCancellationRequested` を
  同じ行に持たないもの → develop で 13 行。うち本件の `LlmGatewayDiagramCoder.cs:29` を除く 12 行は #1608 の作業仕様書「母集合」軸 1 で分類済みで、
  その後の変更は無い（同じ 12 行・同じ分類: リース取得 4 行・公開構成の検証 1 行・印付けの捕捉 2 行・要求処理の内側 4 行・ヘルスチェック 1 行）。**除外**
  （いずれも LLM 呼び出しの縮退ではない。LlmGateway の `CompletionUseCase.cs:106`・`:224`・`EmbedUseCase.cs:81` はゲートウェイの要求の文脈で走る捕捉で、
  呼び出し元の縮退とは別の層）。作業木では本 PR が 1 行を直して 12 行になる。
- 軸 2（LLM ゲートウェイの `/complete` / `CompleteAsync` を呼ぶ側）: `Grep "\"/complete|\.CompleteAsync\("`（試験以外）→ 11 ファイル。
  LlmGateway 自身の 3 ファイル（受け側）を除く 8 ファイルの捕捉:
  - `ConversionService/…/LlmGatewayDiagramCoder.cs:29`: **本件**（型だけ）。
  - `ConversionService/…/LlmGatewayGrpcDiagramCoder.cs:35`: `RpcException or InvalidOperationException && !ct.IsCancellationRequested`。問題なし（AC-6）。
  - `AiAnalysisService/…/RagOrchestrator.cs:351`・`HttpLlmCompletionTransport.cs:45`・`:70`: `HttpRequestException or TaskCanceledException`（`IOException or HttpRequestException`）
    `&& !ct.IsCancellationRequested`。問題なし。
  - `AiAnalysisService/…/GrpcLlmCompletionTransport.cs:44`・`:69`・`:104`: `IsTransportFailure(ex, ct)`（ct で絞る）。問題なし。
  - `GraphService/…/LlmGatewaySuggestionClient.cs:39`・`LlmGatewayClusterSummaryClient.cs:50`: `HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested`。問題なし。
  - `GraphService/…/LlmGatewayGrpcSuggestionClient.cs:43`: `RpcException or InvalidOperationException && !ct.IsCancellationRequested`。問題なし。
- 軸 3（ConversionService の全捕捉）: `git grep -n "catch" -- 'src/knowledge/backend/Services/ConversionService/**'`（試験以外）→ 9 行。
  `RawDocumentFetchedConsumer.cs:71`（未対応形式の恒久失敗）・`:89`（失敗を記録して再送出。縮退ではない）・図のコード化 2 行（上記）・
  `PandocConversionService.cs:85`・`:395`・`PdfTextLayerConverter.cs:193`・`RawSourceResolver.cs:71`・`:72`（一時ファイルの後始末・形式の推定の
  best-effort。LLM 呼び出しではなく、取り消しを伝える経路でもない）。本件以外に該当なし。

- 軸 4（#1621 の監査の追加依頼: **他の Wolverine の受け口で、既定 100 秒または期限なしの外向き呼び出しに頼るもの**）: 受け口の登録
  `git grep -n "AddPlatformWolverineStep<\|UseWolverine(" -- 'src/knowledge' 'src/platform'`（試験以外）→ `UseWolverine` 7 サービス。
  DataSourceService・DocumentService は発行だけ（`DisableConventionalDiscovery` ＋ `RoutePlatformEvent`、段の登録なし）で**除外**。残る 5 サービスの受け口 7 つの
  コンストラクタ依存から外向きの呼び出しを辿った。**いずれの受け口も実行期限は既定 60 秒**（上の事実）で、受け口と依存側に try/catch は無い
  （該当ファイルの `grep -c catch` がすべて 0。例外は受け口の外へ出て Wolverine の再試行 → デッドレター）。

  | 受け口 | 外向きの呼び出し（登録） | 期限 | 判定 |
  | --- | --- | --- | --- |
  | ConversionService `RawDocumentFetchedConsumer` | 図のコード化（REST / gRPC） | 本 PR で 20 秒 / 20 秒 | **本件**（縮退の枝を持つので、受け口の期限より短い期限が要る） |
  | GraphService `GraphDocumentSyncConsumer` | `IGraphContentReader`＝`StorageContentReader`（`AddHttpClient<…>()`・http(s) の本文取得）・オブジェクトストレージ | HttpClient 既定 100 秒 / SDK 既定 | 縮退の枝なし。固まると 60 秒で受け口の ct が立ち、失敗 → 再試行。計画の縮退とは矛盾しないが、原因が「時間切れ」でなく「取り消し」として記録される。**報告** |
  | GraphService `DocumentDeletedConsumer` | DB のみ（`GraphDbContext`） | — | 外向き呼び出しなし。除外 |
  | IngestionService `DocumentUpdatedConsumer` | `IDocumentContentReader`＝`StorageDocumentContentReader`（既定 100 秒）・`IEmbeddingService`（REST `LlmGatewayEmbeddingService`＝既定 100 秒 / gRPC `LlmGatewayGrpcEmbeddingService`＝期限なし）・Qdrant gRPC（`QdrantClient`・期限なし） | 100 秒 / なし | 縮退の枝なし（失敗 → 再試行）。埋め込みは分割数に比例して呼ぶため、大きな文書が**正常系でも** 60 秒の受け口の期限を越え得るかは未測定。**報告** |
  | RetrievalService `DocumentDeletedConsumer` | `IVectorStore`＝`QdrantVectorStore`（Qdrant gRPC・期限なし） | なし | 縮退の枝なし。**報告**（期限なしの gRPC） |
  | WikiService `DocumentSyncConsumer` | `IWikiJsClient`＝`WikiJsGraphQlClient`（`ConfigureWikiJsHttpClient` は `Timeout` を設定しない＝既定 100 秒）・`IWikiContentReader`＝`StorageMarkdownReader`（既定 100 秒） | 100 秒 | 縮退の枝なし。**報告** |
  | WikiService `DocumentDeletedConsumer` | `IWikiJsClient`（既定 100 秒） | 100 秒 | 縮退の枝なし。**報告** |

  本 PR では直さない（射程外。起票は依頼元に委ねる）。

## フォローアップ（射程外・低）

- **固まった pandoc が受け口の待ち行列を占有する時間が延びる**（再監査の指摘）。受け口の実行期限を 60 → 300 秒にしたので、pandoc（自前の期限を持たず受け口の ct に従う）が
  固まると 1 回の試行が最大 300 秒続く。試行は 1 回の配信で 4 回（初回 ＋ 再試行 2・10・30 秒）なので、1 通が受け口を占有し得る時間は最大
  4 × 300 ＋ 42 ＝ **約 1242 秒**（従前は 4 × 60 ＋ 42 ＝ 282 秒）。受け口は 1 通ずつ処理するため（再監査の見立て）、その間は後続の原本の変換が待たされる。
  対処（pandoc に図のコード化と同じく自前の期限を与える等）は依頼元が別 issue で扱う。本 PR では直さない。

## 検証

実測はすべて 2026-09-27、手元（Windows・.NET SDK 10）。結果は PR 本文と報告に記す。

- `dotnet test src/knowledge/backend/backend.slnx`（影響ユニット）。
- `dotnet format <slnx> --verify-no-changes`: `src/knowledge/backend/backend.slnx`・`src/platform/backend/backend.slnx`。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`。
- 全体実行の揺らぎ（再監査の指摘）: T-48 のゲートウェイへの要求の回数は負荷で 1〜2 回に揺れる（1 回目が期限 ＋ 待ち行列の遅れで総枠を越える）。回数は「1 以上 3 以下」で見る（総枠が無ければ 5 回）。
- 変異（1 か所ずつ。修正のコミットの上で書き換え、`git show HEAD:<path> > <path>` で戻す）:
  - M1: REST の捕捉を型だけへ戻す → AC-1・AC-4・AC-8（端から端）の試験が赤。
  - M2: REST の捕捉を型で広げる（`|| ex is TaskCanceledException`）→ AC-2・AC-5 の試験が赤。
  - M3: gRPC の捕捉から `&& !ct.IsCancellationRequested` を外す → AC-6 の対照が赤。
  - M4: gRPC の呼び出しへ ct を渡さない → AC-6 の対照が赤。
  - M5: REST の要求へ ct を渡さない → AC-2・AC-5 の試験が赤。
  - M6: 登録から `c.Timeout = limits.CallTimeout` を外す（1 回の期限なし＝既定 100 秒）→ AC-4・AC-8（端から端）・AC-7（配線）の試験が赤（受け口の期限が先に立つ）。
  - M7: 総枠の判定を無効にする → AC-8 の単体・端から端の試験が赤。
  - M8: gRPC の `Deadline` を外す → AC-7 の gRPC 試験が赤。
  - M9: Program.cs から受け口の期限の方針を外す → AC-9 の配線試験が赤。
