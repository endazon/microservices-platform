---
title: 周期の purger の文書ごとの隔離を時間切れでも保ち、コネクタの時間切れが本文の転送を含むことを記録し、MCP の申告の時間切れの設定を並べる（#1608）
type: spec
status: done
related_ids: [FR-19, UC-11, FR-06, FR-01, UC-04, FR-16, NFR-16, ADR-0037, ADR-0057, ADR-0096, ADR-0024, ADR-0029, IADR-0296, IADR-0083, IADR-0051, IADR-0462]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/03_usecases/01_usecases.md UC-04（接続失敗時は再試行し、継続失敗はアラートする）
  - planning:projects/microservices-platform/07_adr/ADR-0057（削除は本文の実体まで及ぶ）
related_specs:
  - 20260926_issue-1604_refresher-and-sync-loop-timeouts.md
  - 20260926_issue-1598_maintenance-loop-foreign-cancellation.md
issue: "#1608"
---

# 仕様書: 周期の purger の時間切れの隔離・コネクタの期限の性質・MCP の申告の期限の構成（#1608）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`）を一次情報とする。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-19** / **UC-11**（個人資料の 90 日自動物理削除・退職者の完全削除）、FR-06（削除は実体まで及ぶ）、
  **FR-01** / **UC-04**（データソースの定期同期。例外フロー「接続失敗時は再試行し、継続失敗はアラートする」）、**FR-16**（MCP サーバー）
- 関連 ADR: ADR-0037 決定 5（90 日の自動物理削除）、ADR-0057 決定 1（削除は本文の実体まで及ぶ）、ADR-0096（退職者の資料の完全削除）、
  ADR-0024 §2・§5（自己申告の収集）、ADR-0029（gRPC の期限）
- 関連 IADR: IADR-0296 決定 3（定期処理は文書ごとに隔離）、IADR-0083（定期同期ワーカー・#1604 追記）、IADR-0051（増分 watermark）、
  IADR-0462（経路 ④-a・#1604 追記）
- 起点 issue: #1608（#1607＝#1604 の PR の監査で「マージを止めない残り」として挙がった 3 点）

## 受け入れ基準

| # | 基準 | 写像先 |
| --- | --- | --- |
| AC-1 | `DocumentObjectPurger.PurgeIsolatedAsync` は、オブジェクトストレージの時間切れ（`TaskCanceledException`・呼び出し側の ct は立っていない）を**その 1 件の失敗**として隔離する。1 件目が時間切れでも 2 件目以降は消える | `DeletionPropagationTests.定期処理は1件目の時間切れで2件目以降の削除を止めない` |
| AC-2 | 周期の本体（`PrivateNoteMaintenanceService.RunAsync`）は、オブジェクトの時間切れで例外で終わらない。時間切れの資料は行を残し（次周期で再試行）、他の資料は消える | `DeletionPropagationTests.定期処理はオブジェクトの時間切れで周期を打ち切らない` |
| AC-3 | 対照: 呼び出し側の ct による取り消し（周期の停止要求）は隔離に畳まず外へ出し、次の文書へ進まない | `DeletionPropagationTests.定期処理は呼び出し側の取り消しを隔離に畳まず伝える` |
| AC-4 | #1604 の作業仕様書の母集合の分類の誤り（purger を「要求の経路」とした）を、本文を書き換えず日付つき追記 `［2026-09-27 追記 / #1608］` で直す | 同仕様書の追記 |
| AC-5 | IADR-0083 へ日付つき追記: コネクタの 30 秒は本文のダウンロードを含むこと、恒久的に遅い 1 件が watermark を止め続けることを受容するか（とその理由） | 同 IADR の追記 |
| AC-6 | McpServer の `appsettings.json` に `Mcp:DeclarationTimeoutSeconds` を `RefreshIntervalSeconds` の隣に既定値 10 で並べる | `ToolCatalogRefresherTimeoutTests.本番の構成ファイルは申告の収集の期限を既定値で並べる` |
| AC-7 | `GrpcToolDeclarationCollector` の `InfiniteTimeSpan` の分岐（構成から到達できない）を削るか、残す理由をコメントに書く → **削る**。期限は常に `UtcNow + Timeout` | 既存 `GrpcToolDeclarationCollectorTests` T-G8（期限が REST のクライアントの `Timeout` に従う）が緑のまま |

## 設計

### 1. purger（AC-1〜AC-3）

- 捕捉を `when (ex is not OperationCanceledException || !ct.IsCancellationRequested)` にする（#1607 の DataSource の探索・取得と同じ形。新しい判断ではない）。
  呼び出し側の ct はそのまま `PurgeAsync` → `storage.DeleteAsync` へ渡っており、停止要求は従前どおり伝わる。
- S3 の時間切れの形: AWS SDK は `HttpClient` 経由で要求し、`AmazonS3Config.Timeout`（`HttpClient.Timeout`）の経過は `TaskCanceledException`
  （`OperationCanceledException` の派生・内側に `TimeoutException`）として表れ、呼び出し側の ct は立っていない。試験はこの形を器で注入する。
- 試験の器: `RecordingObjectStorageClient` に `DeleteThrows`（URI → 投げる例外。null なら通常どおり消す）を足す。既存の `FailDeleteWhen`（常に
  `InvalidOperationException`）では例外の種類を選べないため。`ResetDeletions` で既定へ戻す。
- AC-1 は**順序を固定するため purger を直接呼ぶ**（周期の本体は候補を DB の順で渡すので 1 件目を選べない）。AC-2 は周期の本体の側から見る
  （直す前は時間切れが `RunAsync` から漏れるので、順序に依らず赤になる）。

### 2. コネクタの期限の性質（AC-5）

- Wiki / SaaS の取得は `GetAsync`（既定 `HttpCompletionOption.ResponseContentRead`）なので、`HttpClient.Timeout` の 30 秒には本文の転送が入る。
- 転送がいつも 30 秒を超える 1 件があると、`SyncResult.ShouldAdvanceWatermark`（`Failed == 0` が条件）が偽のまま、そのソースの `LastSyncedAt` は進まない。
- **受容する**と判断した（IADR-0083 の追記に理由を書く）。要点: UC-04 の例外フロー「再試行し、継続失敗はアラートする」どおりであり
  （連続 5 回で継続失敗アラート・SC-06 に直近エラー）、他の項目の鮮度は止まらず、データも失われない。遅い 1 件を飛ばして位置を進める形は
  その項目を二度と取り直さない（「再試行する」を黙って破る）ので採らない。費用は「止まった位置以降に更新された項目の再取得・再発行」。
  **計画への環流は要らない**（計画の定める挙動は満たしている。変えるとすれば実装の位置の持ち方＝ IADR-0051 の改定である）。

### 3. MCP の申告の期限（AC-6・AC-7）

- `appsettings.json` の `Mcp` 節へ `"DeclarationTimeoutSeconds": 10` を `RefreshIntervalSeconds` の次に足す（値はコードの `DefaultTimeoutSeconds` と同じで、挙動は変えない）。
- 分岐は削る。名前付きクライアントの `Timeout` は `AddMcpToolDeclarationSources` が `ConfiguredTimeout`（`Math.Max(1, …)` 秒）で与え、無期限へ至る構成は無い。
  CLAUDE.md の「起こり得ないケースへの防御的実装」をしない方針に合わせる。コメントの「未構成なら 100 秒」も本番の登録に合わせて直す。
- 試験: 本番の構成ファイルを `ReadRepoFile`（`McpToolsGrpcDeploymentWiringTests` と共用。`internal` にした）で読み、キーが既定値で在ること、
  登録を通すと名前付きクライアントの期限が 10 秒になることを見る。

### 記録

- IADR-0296 決定 3 へ日付つき追記（隔離は時間切れも隔離する）。IADR-0083 へ日付つき追記（AC-5）。IADR-0462 の #1604 追記の後へ日付つき追記（AC-6・AC-7）。
  **新しい IADR 番号は取らない**（欠番を作らない規約・依頼の指示。既存 IADR への追記で足りる）。
- #1604 の作業仕様書へ日付つき追記（AC-4）。
- テスト仕様書: `docs/tests/FR-19_private-notes-lifecycle.md`（21 行目）、`docs/tests/FR-16_mcp-server.md`（R-6 と変異の行）。trace ブロックへ本仕様書と #1608 を足す。

## 母集合（同じ欠陥の走査）

走査は 2026-09-27、`origin/develop` = `f195df1f` の上で行った（行番号は作業木）。対象は `src/platform/**` と `src/knowledge/**` の試験以外
（`/Tests/` と `.Tests/` をパスで除外。`src/ai-stock-trading` は別リポジトリの submodule のため**除外**）。問いは「**周期の経路から呼ばれる、項目ごとに
隔離する補助処理のうち、型だけの絞りで時間切れを素通しするものが他にあるか**」である。

- 軸 1（型だけの絞り）: `git grep -n "is not OperationCanceledException" -- 'src/platform/**' 'src/knowledge/**'` から試験を除く → 29 行（develop・作業木とも。
  本 PR は既存の 1 行に `|| !ct…` を足すだけで行を増減しない）。うち ct / stoppingToken で絞っていない行は **develop 14 行 → 作業木 13 行**（差の 1 行が本 PR の purger）。
  残る 13 行の分類:
  - `PostgresAdvisoryLockLeaseCoordinator.cs:41`・GraphService の `ClusterDetectionLeaseCoordinators.cs:39`・`ClusterSummaryLeaseCoordinators.cs:39`・
    `KnowledgeHealthLeaseCoordinators.cs:44`: 周期の経路だが**項目ごとの隔離ではない**（周期の入口で 1 回リースを取る）。Npgsql の接続・コマンドの時間切れは
    `NpgsqlException`（内側に `TimeoutException`）であって取り消しではないので捕捉される。取り消しが届くのは呼び出し側の ct だけで、それは周期の捕捉が停止要求で
    絞っている（#1598・#1604）。**除外**。
  - `ToolDeclarationSource.cs:197`（公開構成の検証 `loader.Load()`）: 同期処理で取り消しを投げない。#445 のホストを止める挙動。**除外**。
  - `GrpcToolDeclarationCollector.cs:144`・`GrpcServiceIntrospectionCollector.cs:121`（`MarkingTokenProvider`）: 取り消しを印を付けずに通すための捕捉で、
    呼び出し側の収集器が `when (ct.IsCancellationRequested)` を先に置いて畳む。gRPC の期限切れは `RpcException(DeadlineExceeded)` で表れる。**除外**。
  - `SyncConflicts/Get/Endpoint.cs:52`・`CompletionUseCase.cs:106`・`:224`・`EmbedUseCase.cs:81`: 要求処理の内側（LlmGateway の 3 行は、周期の呼び出し元から
    呼ばれても捕捉が走るのはゲートウェイの要求の文脈であり、呼び出し元の周期の隔離には関わらない）。**除外**。
  - `WolverineExtensions.cs:259`: ブローカのヘルスチェック（プローブの要求で走る。定期の health publisher は登録されていない ——
    `git grep -n "IHealthCheckPublisher\|AddHealthCheckPublisher\|HealthCheckPublisherOptions"` 試験以外 0 行）。取り消しが漏れてもヘルスチェックの枠組みが
    Unhealthy として報告する。**除外**。
  - `LlmGatewayDiagramCoder.cs:29`: **周期の経路ではない**が、**同じ形の別の欠陥**を見つけた（下の「射程外の発見」）。本件の射程（周期の purger）からは**除外**。
- 軸 2（項目ごとの隔離を名乗る処理）: `git grep -nE "隔離|[Ii]solat" -- 'src/platform/**/*.cs' 'src/knowledge/**/*.cs'`（試験を除く）→ develop 9 行・作業木 10 行（差の 1 行は本 PR が足したコメント `DocumentObjectPurger.cs:83`）。
  `DocumentObjectPurger.cs` ×3（作業木。本 PR）・その呼び出し元 `PrivateNoteMaintenanceService.cs` ×4（:112・:187・:212・:224。周期の 2 経路＝退職者の完全削除と
  90 日の自動物理削除）・`GrpcServiceIntrospectionCollector.cs:19` と `HttpEffectiveConfigCollector.cs:86`（構成情報 API。ct で絞り済み。問題なし）・
  `KnowledgeHealth/Report/Endpoint.cs:20`（ネットワーク隔離の試験の注記。無関係）。他の隔離の補助処理は無い。
- 軸 3（周期の本体が項目ごとに呼ぶ下流の捕捉）: PrivateNoteMaintenance の周期が所有者・資料ごとに呼ぶ下流を読んだ。
  `HttpPrivateNoteNotifier`・`GrpcPrivateNoteNotifier` はいずれも `when (!IsCallerCancellation(ex, ct))`（呼び出し側の取り消しだけを通す）で問題なし。
  名簿（`ownerRetention.GetAsync`）は #1583 の読み直しで ct 絞り済み（`PrivateNoteMaintenanceService.cs:170`）。狭い型の捕捉
  （`git grep -nE "catch \((HttpRequestException|RpcException|AmazonS3Exception|NpgsqlException|IOException)"` 試験以外 20 行）は gRPC の `RpcException` が大半で、
  gRPC の期限切れは `RpcException(DeadlineExceeded)` なので時間切れを取りこぼさない。周期の経路で時間切れを素通しする狭い捕捉は無い。

### 射程外の発見（本 PR では直さない）

- `LlmGatewayDiagramCoder.CodeAsync`（ConversionService の REST の図コード化。`Services:LlmGatewayGrpc` が未構成のときの既定の実装）は
  `when (ex is not OperationCanceledException)` で、LLM ゲートウェイへの要求の時間切れ（名前付きクライアントの既定 100 秒）が「画像として保持」へ縮退せず、
  `RawDocumentFetchedConsumer`（Wolverine のメッセージの受け口）の正規化全体を失敗させる。計画（UC-06「図コード化（LLM）の失敗は画像保持へ縮退」）と
  同クラスの注記（「呼び出し失敗は例外送出せず画像保持へ縮退する」）に反する。周期の経路ではなく、メッセージの再試行で回復し得るため本件（周期の purger）の射程外とし、
  報告に挙げて扱いを委ねる（gRPC 実装 `LlmGatewayGrpcDiagramCoder` は `RpcException` を ct で絞って捕捉しており問題なし）。

## 検証

実測はすべて 2026-09-27、手元（Windows・.NET SDK 10）。結果は PR 本文と報告に記す。

- `dotnet build` / `dotnet test`: `src/knowledge/backend/backend.slnx`・`src/platform/backend/backend.slnx`。
- `dotnet format <slnx> --verify-no-changes`: 両ユニット。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`。
- 変異（1 か所ずつ。直したコミットの上で書き換え、`git show HEAD:<path> > <path>` で戻す）:
  - purger の捕捉を型だけへ戻す → AC-1・AC-2 の試験が赤、AC-3 は緑。
  - purger の捕捉を「全部捕まえる」へ広げる → AC-3 の試験が赤。
  - appsettings.json から期限のキーを外す → AC-6 の試験が赤。
  - gRPC の収集に期限を付けない（`deadline: null`）→ T-G8 が赤（AC-7 で分岐を削った後も期限が効いていることの確認）。
