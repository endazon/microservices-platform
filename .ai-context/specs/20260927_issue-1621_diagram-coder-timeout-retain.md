---
title: REST の図のコード化が LLM ゲートウェイの時間切れで正規化全体を失敗させる件を直し、図を画像として残す（#1621）
type: spec
status: done
related_ids: [FR-12, UC-06, FR-11, ADR-0010, ADR-0012, ADR-0029, IADR-0008, IADR-0400]
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
  内側に `TimeoutException`）で表れ、呼び出し元の ct は立っていない → 捕捉を抜け、`NormalizationService` → `RawDocumentFetchedConsumer.Handle` の
  `catch (Exception)` でジョブが `failed` になり再送出される（Wolverine の再試行 → 使い切ればデッドレター）。
- `ct` の出所: `RawDocumentFetchedConsumer.Handle(ev, envelope, ct)` → `normalizer.NormalizeAsync(ev, ct)` → `diagramCoder.CodeAsync(figure, confidentiality, ct)` →
  `http.PostAsJsonAsync(…, ct)` / `ReadFromJsonAsync(ct)`。**途中で差し替え・連結はない**ので、`ct` は呼び出し元（メッセージ消費）のものである。
  `IDiagramCoder` の利用者は `NormalizationService` だけ（`git grep -n "IDiagramCoder\|diagramCoder" -- src/knowledge/backend/Services/ConversionService/Features`）。

## 受け入れ基準

| # | 基準 | 写像先 |
| --- | --- | --- |
| AC-1 | REST 実装は、LLM ゲートウェイの時間切れ（`TaskCanceledException`・呼び出し元の ct は立っていない）を `Retain("llm-call-failed")` へ畳む | `LlmGatewayDiagramCoderTests.Retains_when_gateway_times_out`（FR-12 テスト仕様 T-44） |
| AC-2 | 対照: 呼び出し元の ct による取り消し（その ct を運ぶ `TaskCanceledException`）は畳まずに外へ出す | `LlmGatewayDiagramCoderTests.Propagates_caller_cancellation`（T-44） |
| AC-3 | AC-1 の器が本物の時間切れの形（`TaskCanceledException`・内側 `TimeoutException`・ct 未取り消し）を作ること | `LlmGatewayDiagramCoderTests.Hanging_gateway_fixture_produces_the_timeout_shape`（T-44） |
| AC-4 | 受け口から端まで: 時間切れでも図は画像として残り、変換ジョブは `succeeded`（`DiagramsRetained=1`・資産 1 件を発行） | `DiagramCodingTimeoutPipelineTests.Gateway_timeout_keeps_the_figure_as_an_image_and_the_job_succeeds`（UC-06 テスト仕様 T-46） |
| AC-5 | 受け口から端まで（対照）: 呼び出し元の取り消しは受け口の外へ出て、図を画像として保管せず発行もしない | `DiagramCodingTimeoutPipelineTests.Caller_cancellation_propagates_out_of_the_consumer`（T-47） |
| AC-6 | gRPC 実装が同じ性質を持つか確かめる。持つなら試験で固定し、持たなければ直す → **持つ**（直さない）。呼び出し元に由来しない `RpcException(DeadlineExceeded / Cancelled)` は画像保持、呼び出し元の取り消しは `RpcException(Cancelled)` のまま外へ出る。ct が生成クライアントへ渡ることも見る | `LlmGatewayGrpcDiagramCoderTests.呼び出し元に由来しない期限切れと取り消しは画像保持へ縮退する`・`呼び出し元の取り消しは畳まずに外へ出す`（T-45） |
| AC-7 | IADR-0008 決定 B-2 へ日付つき追記（本文は書き換えない）。新しい IADR 番号は取らない | 同 IADR の追記 |

## 設計

- 捕捉を `when (ex is not OperationCanceledException || !ct.IsCancellationRequested)` にする（#1607＝#1604、#1619＝#1608 と同じ形。新しい判断ではない）。
  理由文字列は従前の `llm-call-failed` のまま（gRPC 実装と同じ。運用の集計を輸送で割らない）。
- gRPC 実装（`LlmGatewayGrpcDiagramCoder`）の捕捉は `when (ex is RpcException or InvalidOperationException && !ct.IsCancellationRequested)`
  （`is` のパターン結合子 `or` は `&&` より強く結合するので `(ex is (RpcException or InvalidOperationException)) && !ct…`）。
  チャネル（`GrpcClientExtensions.CreatePlatformChannel`）は `ThrowOperationCanceledOnCancellation` を立てておらず、`HttpClient` ではなく
  ハンドラ直結のため `HttpClient.Timeout` も無い。期限切れ・取り消し・s2s トークン取得（`CallCredentials` 内）の時間切れはいずれも
  `RpcException(DeadlineExceeded / Cancelled)` として届く → **既に正しい**。コメントでこの根拠を明記し、試験で固定する。
- 試験の器: 応答を返さず要求の ct が立つまで待つ `HttpMessageHandler`。時間切れは本物の `HttpClient.Timeout`（100 ms）で起こす。
  呼び出し元の取り消しは、要求が届いた時点でハンドラから呼び出し元の `CancellationTokenSource.Cancel()` を呼んで「要求の途中」で起こす
  （`HttpClient` はそのとき呼び出し元の ct を運ぶ `TaskCanceledException` を投げる）。対照の試験は伝わらなかったときに 100 秒待たないよう
  `Timeout` を 30 秒にしておく。ネットワークは使わない（ハンドラ直結。待ち受けも無い）。
- 端から端の試験は `RawDocumentFetchedConsumer` ＋ `NormalizationService` ＋ `LlmGatewayDiagramCoder` を本物で組み、LLM ゲートウェイ（ハンドラ）・
  本文変換・オブジェクトストレージ・発行口だけを差し替える。ジョブストアは既存試験と同じ EF InMemory。

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

## 検証

実測はすべて 2026-09-27、手元（Windows・.NET SDK 10）。結果は PR 本文と報告に記す。

- `dotnet test src/knowledge/backend/backend.slnx`（影響ユニット）。
- `dotnet format <slnx> --verify-no-changes`: `src/knowledge/backend/backend.slnx`・`src/platform/backend/backend.slnx`。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`。
- 変異（1 か所ずつ。修正のコミットの上で書き換え、`git show HEAD:<path> > <path>` で戻す）:
  - M1: REST の捕捉を型だけへ戻す（`when (ex is not OperationCanceledException)`）→ AC-1・AC-4 の試験が赤。
  - M2: REST の捕捉を型で広げる（`when (ex is not OperationCanceledException || ex is TaskCanceledException)`）→ AC-2・AC-5 の試験が赤。
  - M3: gRPC の捕捉から `&& !ct.IsCancellationRequested` を外す → AC-6 の対照が赤。
  - M4: gRPC の呼び出しへ ct を渡さない（`cancellationToken: default`）→ AC-6 の対照が赤（ct の受け渡しの確認）。
  - M5: REST の要求へ ct を渡さない（`CancellationToken.None`）→ AC-2・AC-5 の試験が赤（ct が呼び出し元のものであることの確認）。
