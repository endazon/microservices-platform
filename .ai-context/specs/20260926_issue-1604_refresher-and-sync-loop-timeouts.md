---
title: McpServer の ToolCatalogRefresher が REST の取得の時間切れ 1 回でホスト全体を止める件と、DataSourceSync の定期同期が接続の時間切れで黙って永久に止まる件を直し、#1598 の周期の試験の抜けを塞ぐ（#1604）
type: spec
status: done
related_ids: [FR-16, FR-01, UC-04, FR-19, FR-22, FR-17, FR-18, FR-10, NFR-16, ADR-0024, ADR-0029, ADR-0035, ADR-0096, IADR-0462, IADR-0379, IADR-0083, IADR-0051, IADR-0053, IADR-0054, IADR-0299, IADR-0425, IADR-0430, IADR-0431]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0024_mcp-server-integration.md §2・§5
related_specs:
  - 20260926_issue-1598_maintenance-loop-foreign-cancellation.md
  - 20260926_1515_mcp-tool-declarations-grpc.md
issue: "#1604"
---

# 仕様書: 常駐ループと時間切れの取り違え（McpServer・DataSourceService）と #1598 の試験の抜け（#1604）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`）を一次情報とする。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-16**（MCP サーバー。ツールの公開は自己申告の定期収集と公開構成の突合で決まる）、**FR-01** / **UC-04**（データソースの定期同期）、
  FR-19 / FR-22 / FR-17 / FR-18 / FR-10（#1598 で直した 4 つの常駐処理。本件は試験だけを足す）
- 関連 ADR: ADR-0024 §2・§5（自己申告の収集。到達できない宛先は「申告なし」＝推測で公開しない）、ADR-0029（gRPC の期限）
- 関連 IADR: IADR-0462（経路 ④-a。gRPC の期限は REST の名前付きクライアントの `Timeout` を引く）、IADR-0083（定期同期ワーカー）、
  IADR-0053 / IADR-0054（Wiki / SaaS コネクタ）、IADR-0299 決定 3 と IADR-0431 決定 5 の #1598 追記（周期のループの形）
- 起点 issue: #1604（#1601＝#1598 の PR の監査の指摘）

## 受け入れ基準

| # | 基準 | 写像先 |
| --- | --- | --- |
| AC-1 | McpServer の REST の申告の収集（`HttpToolDeclarationSource.CollectOneAsync`）は、**呼び出し側の ct による取り消しだけ**を外へ出し、HttpClient の時間切れ（`TaskCanceledException`）は「申告なし」へ畳む | AC-3 の試験 |
| AC-2 | `ToolCatalogRefresher` は**停止要求（`stoppingToken`）のときだけ**ループを抜ける。停止要求の無い取り消しは記録して次の周期を待つ | 偽の収集器が停止要求と無関係な取り消しを投げる試験 |
| AC-3 | 汎用ホストで本物の `ToolCatalogRefresher` と本物の REST の収集器を、**接続を受けて応答しない 127.0.0.1 の待受**へ向け、短い期限で動かす。ホストは止まらず、`ApplicationStopping` は発火せず、次の周期の収集が起きる。対照として**接続を拒否する**宛先でも同じ（時間切れではなく拒否として処理される）。待受は loopback だけ | 新しい試験クラス（2 件） |
| AC-4 | REST の名前付きクライアントに明示の `Timeout` を与える（構成 `Mcp:DeclarationTimeoutSeconds`、既定 10 秒、1 未満は 1）。gRPC の期限は従前どおり同じクライアントの `Timeout` を引く（期限の出所は 1 つのまま） | 登録の試験（REST のクライアントと gRPC が引く値が一致し、構成が効く） |
| AC-5 | `DataSourceSyncHostedService` は停止要求のときだけループを抜ける。停止要求の無い取り消しは記録して次の周期へ進む | 周期を短くしたワーカーの試験 |
| AC-6 | `DataSourceSyncService` の探索（discover）と 1 件ごとの取得（fetch）は、時間切れ（停止要求の無い取り消し）を**そのソースの失敗**として扱う（連続失敗の計数・watermark を進めない）。呼び出し側の ct による取り消しは従前どおり外へ出す | 偽のコネクタで探索・取得が時間切れを投げる試験 |
| AC-7 | Wiki / SaaS コネクタの名前付きクライアントに明示の `Timeout`（30 秒）を与える | 登録の試験（本番の Program.cs の DI から引く） |
| AC-8 | DataSourceSync の周期は構成から来て最短 30 秒に丸められるため、**試験だけが周期を与える口**（`internal CycleInterval`、既定 null＝構成どおり）を足す。本番の組み立てと SC-06 の「次回同期」の位相は変えない | AC-5 の試験がこの口を使う |
| AC-9 | #1598 で直した 4 つの常駐処理（PrivateNoteMaintenance・ClusterDetection・ClusterSummary・KnowledgeHealth）に、偽物が **2 回続けて投げる**場合を足し、失敗の後に**次の拍まで待つ**（間を空けずに再試行しない）ことを固定する（変異 M1「待たずに再試行」を殺す） | 既存の試験クラスへ 1 件ずつ |

## 設計

### 1. McpServer（AC-1〜AC-4）

- `CollectOneAsync` の捕捉を `when (ex is not OperationCanceledException || !ct.IsCancellationRequested)` にする。
  `HttpEffectiveConfigCollector.CollectOneAsync`（#1382）と同じ形で、新しい判断ではない。gRPC 側（`GrpcToolDeclarationCollector`）は既に
  `when (ct.IsCancellationRequested)` を先に置いて同じ意味になっている（変更しない）。
- `ToolCatalogRefresher` の収集の捕捉を `when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)`、
  `Task.Delay` の捕捉を `when (stoppingToken.IsCancellationRequested)` にする。公開構成の検証（`loader.Load()`）の捕捉は同期処理で取り消しを
  投げないため変えない（構成の破損でホストを止める #445 の挙動は保つ）。
- 期限: `Mcp:DeclarationTimeoutSeconds`（既定 10 秒）を名前付きクライアントの `Timeout` に与える。**IADR-0462「経路 ④-a への適用」3 は
  「新しいキーを作ると REST の期限も変えることになり、移行の不変条件を破る」として期限のキーを作らなかった。** 本件はまさに REST の期限
  （既定 100 秒）を変えることが目的であり、移行の段ではなく不具合の是正である。gRPC は同じクライアントの `Timeout` を引き続けるので、
  **期限の出所が 1 つである**という同項の要点は保たれる。IADR-0462 へ日付つき追記で記録する。
  既定を 10 秒にした理由: 構成情報 API の収集（`Introspection:TimeoutSeconds`）の既定 5 秒と同じ桁で、背景処理の収集であり応答を待つ
  利用者は居ない。宛先は 3 つで逐次なので 1 周の最悪は 30 秒、周期の既定 300 秒・最短 10 秒に対して周期が重なっても `Task.Delay` が後に
  来るので周期が詰まることはない。
- 試験の周期: 構成の周期は 10 秒未満に丸められる。`internal TimeSpan? CycleInterval { get; init; }`（null なら構成どおり）を足し、
  試験は `AddHostedService(sp => new ToolCatalogRefresher(...) { CycleInterval = ... })` で与える。形は #1598 の `CycleInterval` と同じ。
- AC-3 の試験: `TcpListener(IPAddress.Loopback, 0)` で待受け、受けた接続を読まずに保持する（応答しない）。期限は 1 秒（下限）。
  ホストは `Host.CreateApplicationBuilder`（既定の `BackgroundServiceExceptionBehavior.StopHost`）に `AddMcpToolDeclarationSources` を通し、
  `ToolCatalog` の `Version`（`Refresh` の回数）が 2 以上になるまで待つ。`IHostApplicationLifetime.ApplicationStopping` に登録した印が立たないこと、
  `ExecuteTask` が完了していないこと、待受が 2 回以上接続を受けたことを測る。
  - 対照（拒否）: 待受を開いてポートを得た後に閉じ、同じポートへ向ける。期限は 5 秒（Windows の loopback の拒否は再送で約 2 秒かかるため、
    期限 1 秒だと拒否が時間切れに化けて対照にならない）。記録された警告の例外が `HttpRequestException` であること（時間切れではない）を測る。
  - 直す前の形へ戻すと、応答しない宛先の試験だけが赤になる（約 1 秒で `ApplicationStopping`）ことを変異で確かめる。
- AC-2 の試験: 1 回目の収集で `TaskCanceledException`（停止要求ではない）を投げる偽の `IToolDeclarationSource` を置き、2 回目の収集が起き、
  `ApplicationStopping` が立たないことを測る。`CollectOneAsync` の修正がある限り REST の経路からは周期の捕捉まで取り消しが届かないので、
  周期の捕捉の変異はこの試験でしか殺せない。

### 2. DataSourceService（AC-5〜AC-8）

- ループ: `catch (OperationCanceledException) { break; }` を `catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }` にし、
  続く `catch (Exception ex)` が停止要求の無い取り消しを周期の失敗として記録する。`WaitForNextTickAsync(stoppingToken)` が停止要求で投げる
  取り消しは外側の `catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)` で静かに終える（#1598 と同じ形）。
- 試験の口: `internal TimeSpan? CycleInterval { get; init; }`。`StartSchedule()`（構成の解決・30 秒の床・SC-06 の位相の記録）はそのまま呼び、
  PeriodicTimer にだけ `CycleInterval ?? interval` を渡す。本番では null。
- `DataSourceSyncService`: 探索と取得の捕捉を `when (ex is not OperationCanceledException || !ct.IsCancellationRequested)` にする。
  手動の `/sync`（要求の ct）でも時間切れはソースの失敗（探索なら DiscoverSucceeded=false、取得なら failed 件数）として応答される。
  利用者が要求を打ち切った取り消し（要求の ct）は従前どおり外へ出す。
- コネクタ: `WikiConnector.HttpClientName` / `SaaSConnector.HttpClientName` を公開の定数にし、`ConnectorHttpTimeout = 30 秒` を
  `AddHttpClient(name, c => c.Timeout = ...)` で与える。値は 1 要求ぶん（SaaS の 429 の待ちは要求の外）。探索が複数ページを引くときは
  ページごとに効く。

### 3. #1598 の試験の抜け（AC-9）

- 周期を 300 ミリ秒にし、偽物が 1・2 回目に投げ、3 回目に成功する（GraphService はリースを渡さない＝本体を読まない、PrivateNote は
  「経過」と答えて資料を消す）。各回の呼び出し時刻を記録し、**1→2 回目・2→3 回目の間隔がそれぞれ周期の半分（150 ミリ秒）以上**あることを測る。
  M1（失敗の後に待たずに再試行する）では間隔が数ミリ秒になる。周期の半分を下限にするのは、PeriodicTimer が前の拍からの位相で刻み、
  Windows のタイマー分解能（約 15 ミリ秒）で前後するため。

### 記録

- IADR-0462 へ日付つき追記（McpServer の時間切れの扱いと期限のキー）。IADR-0083 へ日付つき追記（定期同期ワーカーの取り消しの扱いと
  コネクタの期限）。IADR-0299 決定 3・IADR-0431 決定 5 の #1598 追記へ［#1604］の一文（2 回投げる試験と M1）。**新しい IADR 番号は取らない**
  （欠番を作らない規約・依頼の指示）。
- テスト仕様書: `docs/tests/FR-16_mcp-server.md`（新しい試験クラス）、`docs/tests/UC-04_datasource-registration-sync.md`（ループ・時間切れ・期限）、
  `docs/tests/FR-19_private-notes-lifecycle.md` と `docs/tests/FR-10_dashboard.md`（2 回投げる試験）。

## 母集合（同じ欠陥の走査）

走査は 2026-09-26、`origin/develop` = `840534fb` の上で行った。件数は develop と本 PR の作業木の両方で数えた（行番号は作業木）。
対象は `src/platform/**` と `src/knowledge/**` の試験以外（`src/ai-stock-trading` は別リポジトリの submodule のため**除外**）。
常駐処理の全数（14 件）は #1598 の仕様書の軸 1 で引いたものから変わっていない（本件で常駐処理は増減しない）。

- 軸 1（取り消しを型だけで素通しする捕捉）: `git grep -n "is not OperationCanceledException"` → 試験以外 develop 29 行・作業木 29 行
  （本 PR は既存の 4 行に `|| !ct…` を足すだけで行を増減しない）。
  - 本 PR で直す: `ToolDeclarationSource.cs`（`CollectOneAsync`・周期の捕捉）、`DataSourceSyncService.cs`（探索・取得）。
  - 既に ct で絞り済み（問題なし）: `PrivateNoteMaintenanceService.cs` ×2・GraphService の 3 つ・`SecretWriteRecordStore.cs` ×2・
    `ScopeUserAttributeSource.cs`・`AuthzScopeHttpClient.cs`・`DriftDetectionHostedService.cs`・`HttpEffectiveConfigCollector.cs`。
  - `ToolDeclarationSource.cs` の公開構成の検証（`loader.Load()`）: 同期処理で取り消しを投げない（**除外**。#445 のホストを止める挙動）。
  - `GrpcToolDeclarationCollector.cs` の `MarkingTokenProvider`: 取り消しを印を付けずに通すための捕捉で、呼び出し側の収集器が
    `when (ct.IsCancellationRequested)` を先に置いて畳む（**除外**）。`GrpcServiceIntrospectionCollector.cs` の同じ形も同じ理由で除外。
  - リースの取得（`PostgresAdvisoryLockLeaseCoordinator.cs`・GraphService の 3 つの `*LeaseCoordinators.cs`）: 取り消しは呼び出し側の
    周期の捕捉へ届き、そこが停止要求で絞られている（GraphService は #1598、DataSource は本 PR）ので周期は止まらない（**除外**）。
  - 要求処理の内側（`LlmGatewayDiagramCoder.cs`・`DocumentObjectPurger.cs`・`SyncConflicts/Get/Endpoint.cs`・`CompletionUseCase.cs` ×2・
    `EmbedUseCase.cs`・`WolverineExtensions.cs`）: 常駐ループの寿命を決めない（**除外**。要求の ct と時間切れの取り違えは応答の種類の問題で、
    本件の「ループが止まる／ホストが落ちる」とは別。起票はしない）。
- 軸 2（型だけの `catch (OperationCanceledException)`）: `git grep -nE "catch\s*\(\s*(System\.)?(OperationCanceledException|TaskCanceledException)"` →
  試験以外 develop 15 行。絞りの無いものは `DataSourceSyncHostedService.cs:34`（**本 PR で直す**）と `DriftDetectionHostedService.cs:59`（外側で
  `WaitForNextTickAsync(ct)` だけを包む。#1598 で問題なしと判定済み）の 2 行。他 13 行は `when` で絞っている。
  作業木では 17 行 = 15 ＋ 本 PR が足した外側の捕捉 1 行（`DataSourceSyncHostedService.cs:58`）＋ 本 PR のコメントが旧形を引用した 1 行（同 :42）。
  絞りの無い捕捉は `DriftDetectionHostedService.cs:59` の 1 行だけになった。
- 軸 3（常駐ループが使う名前付き HttpClient の期限）: `git grep -nE "AddHttpClient\("` → 試験以外 develop 36 行・作業木 36 行（既存行の書き換えのみ）。常駐ループから呼ばれるものは
  McpServer の申告（**本 PR で明示**）・DataSourceService の Wiki / SaaS（**本 PR で明示**）・構成情報 API（`HttpEffectiveConfigCollector` が
  生成時に `Timeout` を与える。問題なし）・GraphService の `HttpKnowledgeHealthReporter`（周期の捕捉が #1598 で停止要求に絞られ、時間切れは
  周期の失敗になる。100 秒の既定は周期 1 時間に対して止まる原因にならない。**除外**）・s2s トークンの取得（`GrpcClientExtensions.cs`。
  gRPC の CallCredentials の中で呼ばれ、呼び出しの期限に包まれる。**除外**）。他は要求処理の経路（BFF・各サービスの Program.cs）で
  常駐ループの寿命を決めない（**除外**）。

## 検証

実測はすべて 2026-09-26〜27、手元（Windows・.NET SDK 10）。

- 全件: `McpServer.Tests` **207/207**、`DataSourceService.Tests` **330/330**、`GraphService.Tests` **647/647**、`DocumentService.Tests` **632/632**。
  `dotnet format <slnx> --verify-no-changes --include <変更したファイル>` は両ユニットで差分なし。
  `node scripts/check-test-spec-coverage.js` OK（記載の対 343 件が床と一致）、`node scripts/check-trace-blocks.js` OK。
- 時間に依る新しい試験の安定性（`--no-build` で連続実行）: `ToolCatalogRefresherTimeoutTests`（7 件）**10/10 回**、
  `DataSourceSyncHostedServiceTests`（4 件）・`BatchLoopForeignCancellationTests`（6 件）・`PrivateNoteMaintenanceHostedServiceTests`（2 件）各 **10/10 回** 全件緑。
- 変異（1 か所ずつ sed で書き換えて実行し、退避した写しで元へ戻した）:

  | 変異 | 実行した試験 | 赤 |
  | --- | --- | --- |
  | McpServer: 収集の捕捉・周期の捕捉・待ちの捕捉をすべて直す前へ戻す | `ToolCatalogRefresherTimeoutTests` | **3/7**（応答しない宛先 [1 s。`ApplicationStopping`]・漏れた取り消し・停止要求。拒否の対照と期限の 3 件は緑） |
  | McpServer: 収集の捕捉だけ戻す | 同上 | **1/7**（応答しない宛先。周期の捕捉が記録するのでホストは止まらないが、申告なしへ畳まれず 30 秒の見張りで赤） |
  | McpServer: 周期の捕捉だけ戻す | 同上 | **2/7**（漏れた取り消し・停止要求） |
  | McpServer: 名前付きクライアントに期限を与えない | 同上 | **4/7**（期限の 3 件＋応答しない宛先） |
  | McpServer: `Task.Delay` の捕捉を型だけに戻す | 同上 | **0/7**（等価。待ちは停止要求の取り消ししか受け取らない） |
  | DataSource: ループの捕捉を型だけに戻す | `DataSourceSyncHostedServiceTests` | **1/4**（ループの試験。10 秒の時間切れ） |
  | DataSource: 失敗の後に待たずに再試行する（M1） | 同上 | **1/4**（ループの試験。間隔の表明） |
  | DataSource: 探索の絞り込みを戻す | `DataSourceSyncServiceTests` | **1/15**（探索の時間切れ） |
  | DataSource: 取得の絞り込みを戻す | 同上 | **1/15**（取得の時間切れ） |
  | DataSource: コネクタの期限を外す | `DataSourceSyncHostedServiceTests` | **1/4**（期限の試験） |
  | GraphService: クラスタ検出に M1 | `BatchLoopForeignCancellationTests` | **1/6**（当てた 1 つの拍の試験だけ） |
  | GraphService: クラスタ要約に M1 | 同上 | **1/6**（同上） |
  | GraphService: 健全性の報告に M1 | 同上 | **1/6**（同上） |
  | DocumentService: 個人資料の保守に M1 | `PrivateNoteMaintenanceHostedServiceTests` | **1/2**（拍の試験） |

  M1 は周期の本体の呼び出しを「失敗なら即座に再試行する内側のループ（最大 5 回）」で包む形で当てた。#1598 の既存の試験（次の周期が来ること）は
  M1 の下でも緑のままであり、拍の試験だけが赤になる（監査の指摘どおり）。
