---
title: 呼び出し側の取り消しの対照を本物の HttpClient・SDK の形（TaskCanceledException）で起こし、型で判定する変異を落とす（#1630）
type: spec
status: done
related_ids: [NFR-16, FR-15, FR-01, UC-04, SC-17, FR-05, FR-09, FR-06, FR-12, NFR-09, ADR-0029, ADR-0115, ADR-0088, ADR-0014, IADR-0029, IADR-0462, IADR-0473, IADR-0461, IADR-0413, IADR-0296, IADR-0299, IADR-0431]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/02_requirements FR-15（構成情報 API・ドリフト検出）
  - planning:projects/microservices-platform/03_usecases/01_usecases.md UC-04（接続失敗時は再試行し、継続失敗はアラートする）
related_specs:
  - 20260927_issue-1622_deterministic-tick-tests.md
  - 20260911_issue-1382_drift-timeout-stops-host.md
  - 20260926_issue-1573_department-attribute-follows-group.md
issue: "#1630"
---

# 仕様書: 呼び出し側の取り消しの対照の形（#1630）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`）を一次情報とする。
> 本件は**試験の器の是正**であり、計画の受け入れ基準・本番の挙動は変えない（本番コードの差分は 0 行）。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-15** / **NFR-16**（構成情報 API の自己申告の収集。`HttpEffectiveConfigCollector`）、**FR-01** / **UC-04**（データソースの同期）、
  **SC-17** / **FR-05** / **FR-09**（部門の同期）、**FR-06** / **FR-12**（オブジェクトストレージのバケットの存在確認）、**NFR-09**（s2s トークン）
- 関連 ADR: ADR-0029、ADR-0115 決定 3、ADR-0088 決定 2、ADR-0014
- 関連 IADR: IADR-0029・IADR-0462（収集器）、IADR-0473（部門の同期）、IADR-0461 決定 11（バケットの存在確認）、IADR-0413（認可スコープのクライアント）
- 起点 issue: #1630（#1622＝PR #1627 の作業で射程外として見つけた穴。コメントで #1627 の監査の残り 3 点を追加）

## 事実（着手前の実測）

- `HttpEffectiveConfigCollectorTests.呼び出し側の取り消しは到達不能へ化けずに外へ出る`（#1382）は、呼び出し側の token を持つ**素の**
  `OperationCanceledException` を注入していた。`HttpClient` は内側が投げた取り消しの token が呼び出し側の token と同じなら包み直さずに通すので、
  この注入は収集器まで素の OCE のまま届く。実物の呼び出し側の取り消しは `HttpClient` を通ると `TaskCanceledException` として届く。
- そのため、絞り込みを「`TaskCanceledException` なら期限切れ」と**型で**判定する変異（`|| ex is TaskCanceledException`）が生き残る。
  **実測で緑**（下の変異の記録 MA・直す前: 16/16 合格）。
- `DataSourceSyncServiceTests.Sync_CallerCancellation_Propagates(onDiscover: false)` が取得の捕捉の型の変異を落とせているのは、変異の下では
  次の件の手前の `ct.ThrowIfCancellationRequested()` が素の OCE を投げ直し、**型が違う**からにすぎない（実測: `Expected a <TaskCanceledException> …
  but found <System.OperationCanceledException>`）。後段が同じ型で投げ直せば緑になる（下の ME2 で実測）。

## 母集合（規則 9: 誤りの側の文字列で全文書を走査する）

走査はすべて `origin/develop`（1bcb0a3b）に対して、両ユニットの試験（`src/platform/backend/**` と `src/knowledge/backend/**` の `Tests/`・`*.Tests/`）へ行った。
`src/ai-stock-trading`（submodule・別リポジトリ）は対象外。

- 軸 1（注入の形）: `git grep -n "new OperationCanceledException"` → **11 行**。`ThrowIfCancellationRequested`（コメント以外）→ **0 行**。`FromCanceled` → **0 行**。
- 軸 2（取り消しを外へ出すことの表明）: `git grep -n "ThrowAsync<OperationCanceledException>\|ThrowsAnyAsync<OperationCanceledException>\|ThrowAsync<TaskCanceledException>"` → **21 行**。
- 軸 3（型を含む試験ファイル）: `git grep -l "OperationCanceledException\|TaskCanceledException"` → **29 ファイル**。軸 1・2 に出ない 11 ファイル
  （`RagOrchestratorScopeTests`・`DataSourceSyncHostedServiceTests`・`PlatformUserDirectoryTests`・`PrivateNoteMaintenanceHostedServiceTests`・
  `FakeUserDirectoryClient`・`BatchLoopForeignCancellationTests`・`GraphAccessResolverTests`・`BffScopeResolveWarnTests`・`DriftDetectionChainTests`・
  `ToolCatalogRefresherTimeoutTests` の周期の試験 ほか）は、**時間切れの側**（停止要求ではない `TaskCanceledException`）の注入か、偽物の中で `Task.Delay(…, ct)` が
  本物の取り消しを起こす形で、呼び出し側の取り消しを素の OCE で注入していない。

### 対象（直す）

| # | 対照 | 守る絞り込み | 実物の取り消しの形 | 直し方 |
| --- | --- | --- | --- | --- |
| 1 | `HttpEffectiveConfigCollectorTests.呼び出し側の取り消しは到達不能へ化けずに外へ出る` | `HttpEffectiveConfigCollector.CollectOneAsync`（L88） | HttpClient → `TaskCanceledException` | **本物の HttpClient**（`SocketsHttpHandler`・期限 30 秒）で 127.0.0.1 の応答しない待受へ収集し、呼び出し側の ct を 300 ミリ秒で取り消す |
| 2 | 同 `キャンセルは到達不能へ化けさせず伝播する` | 同上 | 同上 | 同上（収集の前から取り消し済みの ct） |
| 3 | `DepartmentAttributeSyncTests.Host_cancellation_mid_cycle_aborts_without_counting_user_failures` | `DepartmentAttributeSync` の周期ごと中断の枝（L102） | 実物 `KeycloakIdentityAdminClient` は HttpClient → `TaskCanceledException` | 呼び出し側の token を持つ `TaskCanceledException` を注入し、`BeSameAs` |
| 4 | `S3ObjectStorageClientEnsureBucketTests.取り消しは握らずに投げる` | `S3ObjectStorageClient.ProbeBucketAsync`（L227） | AWS SDK（HttpClient）→ `TaskCanceledException`（#1622 の `DocumentObjectPurger` と同じ判断） | HeadBucket の最中に取り消し、呼び出し側の token を持つ `TaskCanceledException` を投げ、`BeSameAs` |
| 5 | `AuthzScopeHttpClientTests.The_callers_cancellation_still_propagates` | `ServiceTokenHandler`（L67） | 実物 `ClientCredentialsServiceTokenProvider` は `SemaphoreSlim.WaitAsync(ct)`・HttpClient → いずれも `TaskCanceledException` | 受け取った ct を持つ `TaskCanceledException` を投げる偽物にし、外へ出たのがそれそのもの（HttpClient が包むならその内側）であることを表明 |
| 6 | `DataSourceSyncServiceTests.Sync_CallerCancellation_Propagates`（issue コメント 2） | `DataSourceSyncService` の探索・取得（L95・L170） | — | 接続子が投げた取り消しを `Injected` として持たせ、`BeSameAs` |

### 除外（理由つき）

| 箇所（軸） | 除外の理由 |
| --- | --- |
| `PrivateNoteDepartedOwnerPurgeTests.cs:392`（軸 1・2） | 実物の `GrpcOwnerRetentionDirectory` は呼び出し側の取り消しを `ct.ThrowIfCancellationRequested()` で**素の OCE** として出す（`GrpcOwnerRetentionDirectoryTests` T-RD-13）。注入の形は実物どおり（#1622 と同じ判断） |
| `GrpcPrivateNoteNotifierTests.cs:121`・`GrpcDocumentTagWriterTests.cs:132`・`GrpcKnowledgeHealthReporterTests.cs:96`・`GrpcTagDictionaryReaderTests.cs:84`（軸 1・2） | gRPC の偽のクライアント。本物のチャネルは呼び出し側の取り消しを `RpcException(Cancelled)` で投げる（`ThrowOperationCanceledOnCancellation` は既定の false。`GrpcServiceIntrospectionCollector` L69・`FakeUserDirectoryClient` の注記・`GrpcOwnerAccountDirectory` の注記）。s2s トークン取得の例外も `CallCredentials` の中で `RpcException` に包まれる（`IntrospectionGrpcTests` T-15 が `Status.DebugException` を辿っている）。**実物の経路に `TaskCanceledException` は出てこない**ので、本件の「HttpClient・SDK の形に揃える」には当たらない（下の「射程外の観察」） |
| `WolverineBrokerHealthAggregationTests.cs:160`・`:175`（軸 1・2） | 絞り込みは `when (ex is not OperationCanceledException)` の**型だけ**で、呼び出し側の ct と突き合わせない（`TaskCanceledException` も素通しする設計）。試験も呼び出し側の token を取り消しておらず、「停止要求と時間切れを ct で分ける」対照ではない。実物は RabbitMQ.Client（AMQP）で HttpClient・SDK の経路でもない |
| `PrivateNoteNotificationDispatchTests.cs:117`・`KnowledgeHealthProducerTests.cs:295`（軸 2） | 既に `TaskCanceledException` を HTTP ハンドラの中から投げ、本物の `HttpClient` を通している（外へ出るのは HttpClient が作る `TaskCanceledException`） |
| `GrpcOwnerAccountDirectoryTests.cs:112`・`GrpcOwnerRetentionDirectoryTests.cs:172`（軸 2） | 偽物が `Task.Delay(Infinite, ct)` で本物の取り消し（`TaskCanceledException`）を起こすか、実物どおり `RpcException(Cancelled)` を投げる。注入していない |
| `GrpcToolDeclarationCollectorTests.cs:249`・`IntrospectionGrpcTests.cs:328`（軸 2） | 本物のチャネルで本物の取り消しを起こしている |
| `DataSourceSyncServiceTests.cs:360`・`DeletionPropagationTests.cs:357`・`ToolCatalogRefresherTimeoutTests.cs:90`・`BffScopeResolveTests.cs:200`（軸 2） | 既に `TaskCanceledException`（呼び出し側の token つき）で起こしている（#1622 ほか）。`DataSourceSyncServiceTests` は上の対象 6（`BeSameAs`）で別途直す |

突き合わせ（数えは `origin/develop` の走査の生の出力に対して行った）:

- 軸 1 の 11 行 ＝ 対象 5 行（収集器 2・部門・S3・認可スコープ）＋ 除外 6 行（`PrivateNoteDepartedOwnerPurgeTests` 1・gRPC の偽物 4・Wolverine 1）。
- 軸 2 の 21 行 ＝ 対象 6 行（収集器 2・部門・S3・認可スコープ・`DataSourceSyncServiceTests`）＋ 除外 15 行（上の表の軸 2 の行: 1＋4＋1＋2＋2＋2＋3）。

## 受け入れ基準

| # | 基準 | 写像先 |
| --- | --- | --- |
| AC-1 | 収集器の 2 つの対照は、本物の HttpClient を 127.0.0.1 の応答しない待受へつなぎ、呼び出し側が取り消す。外へ出る例外が `TaskCanceledException` で呼び出し側の token を持つこと（前提の表明）と、到達不能として記録しないことを表明する | 対象 1・2 |
| AC-2 | 型で判定する変異（`|| ex is TaskCanceledException`）を収集器の絞り込みへ当てると、対象 1・2 が赤になる（直す前は緑） | 変異 MA |
| AC-3 | 掃き出しの対象 3〜5 は呼び出し側の取り消しを HttpClient・SDK の形で起こし、同型の変異（部門の同期の枝は `&& oce is not TaskCanceledException`）を当てると赤になる（直す前は緑） | 変異 MB・MC・MD |
| AC-4 | `DataSourceSyncServiceTests` の取得の対照は、外へ出たのが接続子の投げた取り消しそのもの（`BeSameAs`）であることを表明する。後段が同じ型で投げ直す変異（ME2）でも赤になる（直す前は緑） | 変異 ME・ME2 |
| AC-5 | `GrpcKnowledgeHealthReporterTests` の固定時計の注記から、古くなった「ライブラリを足さない（ratchet に触れる）」を除き、ratchet が縛るのは不採用ライブラリだけであることを書く | 注記 |
| AC-6 | IADR-0296・0299・0431 の #1622 の追記の空白の抜け 3 か所を直す（既存の日付つき追記の中の誤字の修正であり、凍結された本文の書き換えではない） | IADR 3 本 |
| AC-7 | 変更した試験クラスを 20 回連続で実行してすべて合格する | 反復の記録 |

## 設計

- 待受は McpServer の `ToolCatalogRefresherTimeoutTests.SilentPeer`（#1627）と同じ形の入れ子の型を収集器の試験に置く（接続を受けて読みも返しもしない
  `TcpListener(IPAddress.Loopback, 0)`）。共通の試験基盤へは出さない（使うのが 2 つの試験クラスで、プロジェクトもユニットも違う）。
- 対象 5 は型の表明だけでは足りない（実測）。変異が畳んだ `HttpRequestException` を、呼び出し側の token が立っているので `HttpClient` が取り消しへ
  包み直すため、`ThrowAsync<OperationCanceledException>` は変異の下でも通る。**外へ出たのがトークン取得の取り消しそのもの（HttpClient が包むなら内側）**で測る。
- 対象 3・4・6 も `BeSameAs` を足す（#1622 の `DeletionPropagationTests` と同じ理由: 後段が取り消しを投げ直しても緑にならない）。

## 変異の記録

1 か所ずつ当て、該当の試験クラスを走らせ、退避しておいた原本で戻した（戻した後 `git diff --quiet -- <src>` が真であることを毎回確かめた）。
器: `scratchpad/mut.js`・`run_mut.sh`・`mut_all.sh`・`me2.sh`（作業用。コミットしない）。

| 変異 | 当てた箇所 | 走らせた試験クラス | 直す前の試験 | 本 PR の試験 |
| --- | --- | --- | --- | --- |
| MA | `HttpEffectiveConfigCollector.cs` L88 に `|| ex is TaskCanceledException` | `HttpEffectiveConfigCollectorTests` | 緑（16/16） | **赤 2 件**（対象 1・2。`Expected a <TaskCanceledException> to be thrown … but no exception was thrown.`） |
| MB | `DepartmentAttributeSync.cs` L102 を `catch (OperationCanceledException oce) when (ct.IsCancellationRequested && oce is not TaskCanceledException)` | `DepartmentAttributeSyncTests` | 緑（31/31） | **赤 1 件**（対象 3。`but no exception was thrown.`） |
| MC | `S3ObjectStorageClient.cs` L227 に `|| ex is TaskCanceledException` | `S3ObjectStorageClientEnsureBucketTests` | 緑（10/10） | **赤 1 件**（対象 4。`but no exception was thrown.`） |
| MD | `AuthzScopeHttpClient.cs` L67 に `|| ex is TaskCanceledException` | `AuthzScopeHttpClientTests` | 緑（5/5） | **赤 1 件**（対象 5。型の表明は通り、同一性の表明で赤: `Expected (thrown == token.Thrown \|\| …) to be True … but found False.`） |
| ME | `DataSourceSyncService.cs` L170 に `|| ex is TaskCanceledException` | `DataSourceSyncServiceTests` | 赤 1 件（型が違うだけ: `but found <System.OperationCanceledException>`） | 赤 1 件（同上。先に型の表明で落ちる） |
| ME2 | ME ＋ L117 の `ct.ThrowIfCancellationRequested();` を `await Task.Delay(0, ct);`（同じ意味で `TaskCanceledException` を投げ直す形） | `DataSourceSyncServiceTests` | **緑（16/16。生き残る）** | **赤 1 件**（`onDiscover: False`。`BeSameAs` で赤: `Expected (act to refer to System.Threading.Tasks.TaskCanceledException: A task was canceled. …`） |

## 反復の記録（AC-7）

`scratchpad/repeat.sh`: 3 プロジェクトを 1 度ビルドし、`dotnet test --no-build --filter …` を 20 回連続で走らせた。

| 試験クラス | 結果 |
| --- | --- |
| `HttpEffectiveConfigCollectorTests`・`S3ObjectStorageClientEnsureBucketTests`・`AuthzScopeHttpClientTests` | 20/20 |
| `DepartmentAttributeSyncTests` | 20/20 |
| `DataSourceSyncServiceTests` | 20/20 |

## 試験仕様書

- FR-15: 収集器の試験クラスを表へ足した（自己申告の REST の収集: 到達不能への隔離・期限切れ・呼び出し側の取り消し）。`check-test-spec-coverage.js --update` で床を上げた。
- UC-04 T-40・SC-17 T-52: 取り消しの起こし方と「外へ出たのが注入した取り消しそのもの」の表明を追記した。
- `S3ObjectStorageClientEnsureBucketTests`・`AuthzScopeHttpClientTests` はもともと試験仕様書に載っていない基盤の試験（記載義務を負わない warn の側）で、本件では足さない。

## 射程外の観察（フォローアップ候補）

- gRPC の 4 つのクライアント（`GrpcDocumentTagWriter`・`GrpcTagDictionaryReader`・`GrpcKnowledgeHealthReporter`・`GrpcPrivateNoteNotifier`）は
  `catch (RpcException)` を先に置いており、本物のチャネルが呼び出し側の取り消しを `RpcException(Cancelled)` で投げると、それを縮退（到達不能等）へ畳む。
  各試験の「呼び出し元のキャンセルは伝播する」は素の OCE を注入しており、本物のチャネルが通らない経路だけを見ている。
  `GrpcServiceIntrospectionCollector` は `catch (Exception) when (ct.IsCancellationRequested)` を先に置いて揃えている。**本件の射程（HttpClient・SDK の形）の外**であり、
  畳んで困る実害があるか（呼び出し元の要求が既に打ち切られている）の判断を含むため、ここでは直さない。

## 結果

- AC-1〜AC-7 を満たした。本番コードの差分は 0 行。新しい IADR は起こしていない（IADR 3 本は誤字の修正だけ）。
