---
title: 常駐処理の「次の拍まで待つ」試験を壁時計の間隔でなく偽の時計の拍で判定し、呼び出し側の取り消しの対照を HttpClient・AWS SDK の形（TaskCanceledException）で注入する（#1622）
type: spec
status: done
related_ids: [FR-19, FR-22, UC-11, FR-17, FR-18, FR-10, FR-01, UC-04, FR-16, ADR-0035, ADR-0096, ADR-0024, ADR-0057, IADR-0299, IADR-0431, IADR-0083, IADR-0462, IADR-0296]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/03_usecases/01_usecases.md UC-04（接続失敗時は再試行し、継続失敗はアラートする）
related_specs:
  - 20260926_issue-1604_refresher-and-sync-loop-timeouts.md
  - 20260926_issue-1598_maintenance-loop-foreign-cancellation.md
  - 20260927_issue-1608_purger-timeout-isolation.md
issue: "#1622"
---

# 仕様書: 「次の拍まで待つ」試験の決定化と、呼び出し側の取り消しの対照の形（#1622）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`）を一次情報とする。
> 本件は**試験の器の是正**であり、計画の受け入れ基準・本番の挙動は変えない。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-19** / **FR-22** / **UC-11**（個人資料の日次保守）、**FR-17** / **FR-18** / **FR-10**（GraphService の 3 つの定期処理）、
  **FR-01** / **UC-04**（データソースの定期同期）、**FR-16**（MCP サーバーの申告の収集）
- 関連 ADR: ADR-0035 決定 3（クラスタ検出は日次バッチ）、ADR-0096（退職者の資料の完全削除）、ADR-0057（削除は本文の実体まで）、ADR-0024 §2・§5
- 関連 IADR: IADR-0431 決定 5・IADR-0299 決定 3（#1598・#1604 追記）、IADR-0083（#1604 追記）、IADR-0462（#1604 追記）、IADR-0296 決定 3（#1608 追記）
- 起点 issue: #1622（#1607＝#1604 の PR の監査と #1608 の作業で 2 回指摘。コメントで #1619 の監査の穴を追加）

## 事実（着手前の実測）

- `PrivateNoteMaintenanceHostedServiceTests.周期の失敗が続いても次の拍まで待ってから再び判定する` は、監査で DocumentService 全体の実行 3 回中 2 回、
  develop でも落ちる（issue コメント）。
- 原因: 周期 300 ミリ秒の `PeriodicTimer` は**前の拍からの位相**で刻む。1 周期目の本体（スコープ生成・DB 読み）が負荷で 300 ミリ秒を超えると、
  溜まっていた拍がすぐに発火し、2 回目の判定が 1 回目の直後に来る。壁時計の間隔 ≥ 150 ミリ秒の表明は、**正しい実装でも**破れる。

## 母集合（規則 9: 誤りの側の文字列で全文書を走査する）

### A. 壁時計の間隔で「次の拍まで待つ」を測る試験

走査: `git grep -n "/ 2, \"\|次の拍\|TickCycle\|Stopwatch" -- 'src/**/*Tests.cs'`、および `PeriodicTimer` を使う本番コード全数
（`git grep -ln PeriodicTimer -- src`）の各試験クラスを確認。

| # | 試験 | 常駐処理 | 判定 |
| --- | --- | --- | --- |
| 1 | `PrivateNoteMaintenanceHostedServiceTests.周期の失敗が続いても次の拍まで待ってから再び判定する` | `PrivateNoteMaintenanceHostedService` | **対象** |
| 2 | `BatchLoopForeignCancellationTests.クラスタ検出は失敗が続いても次の拍まで待って回す` | `ClusterDetectionHostedService` | **対象** |
| 3 | 同 `クラスタ要約は失敗が続いても次の拍まで待って回す` | `ClusterSummaryHostedService` | **対象** |
| 4 | 同 `ナレッジ健全性の報告は失敗が続いても次の拍まで待って回す` | `KnowledgeHealthHostedService` | **対象** |
| 5 | `DataSourceSyncHostedServiceTests.Loop_SurvivesForeignCancellation_AndWaitsForTheNextTickAfterEachFailure` | `DataSourceSyncHostedService` | **対象** |

除外（理由つき）:

- `ToolCatalogRefresherTimeoutTests`（McpServer）: 間隔の表明を持たない（「2 回目の収集が起きる」を 30 秒の上限つきで待つだけ）。周期は
  `PeriodicTimer` ではなく `Task.Delay` で、溜まった拍が即発火する性質も無い。**揺れの形に当たらない**。
- `DriftDetectionChainTests`・`DepartmentAttributeSyncTests`・`UsageRetentionHostedService`・`NotificationMaintenanceHostedService`: 拍の間隔を測る試験を持たない。
- `AskStreamFirstTokenMetricsTests`・`GrpcCompleteStreamTests`・`DocumentUpdatedFanOutTests`・`QueueOverrideFanOutTests` の `Stopwatch`: 常駐処理の拍ではない（計測値・上限つきの待ち）。

### B. 呼び出し側の取り消しの対照が素の `OperationCanceledException` を注入している箇所

走査: `git grep -n "new OperationCanceledException(\|ThrowIfCancellationRequested()" -- 'src/**/Tests/**'`（DocumentService・DataSourceService・McpServer）、
および McpServer の REST の収集器 `HttpToolDeclarationSource.CollectOneAsync` の対照の有無。

| # | 対照 | 守る絞り込み | 実物の取り消しの形 | 判定 |
| --- | --- | --- | --- | --- |
| 1 | `DeletionPropagationTests.定期処理は呼び出し側の取り消しを隔離に畳まず伝える` | `DocumentObjectPurger.PurgeIsolatedAsync` | AWS SDK（HttpClient）→ `TaskCanceledException` | **対象** |
| 2 | `DataSourceSyncServiceTests.Sync_CallerCancellation_Propagates`（探索） | `DataSourceSyncService` の探索の捕捉 | コネクタの HttpClient → `TaskCanceledException` | **対象** |
| 3 | （無し）取得の捕捉の対照 | `DataSourceSyncService` の 1 件の取得の捕捉 | 同上 | **対象**（対照を足す） |
| 4 | （無し）REST の申告の収集の対照 | `HttpToolDeclarationSource.CollectOneAsync` | HttpClient → `TaskCanceledException` | **対象**（対照を足す） |

除外（理由つき）:

- `PrivateNoteDepartedOwnerPurgeTests.削除の直前の読み直しでの定期処理の取り消しは見送りに畳まず伝える`: 実物の `GrpcOwnerRetentionDirectory` は
  呼び出し側の取り消しを `ct.ThrowIfCancellationRequested()` で**素の `OperationCanceledException`** として出す（同試験 T-RD-13）。注入の形は実物どおり。
- `GrpcToolDeclarationCollectorTests` T-G9: 本物のチャネルで本物の取り消しを起こしており、注入していない。
- `HttpEffectiveConfigCollectorTests.呼び出し側の取り消しは到達不能へ化けずに外へ出る`（Platform.Shared・#1382）: 同じ穴を持つが、#1604 の射程外
  （共通基盤）。報告でフォローアップとして挙げる。

## 受け入れ基準

| # | 基準 | 写像先 |
| --- | --- | --- |
| AC-1 | 母集合 A の 5 つの常駐処理は、周期の拍の源を差し替える試験だけの口（`internal TimeProvider CycleClock`、既定 `TimeProvider.System`）を持つ。`PeriodicTimer` はこの時計から作る。本番の組み立て・周期・挙動は変えない | 各常駐処理 |
| AC-2 | 母集合 A の 5 件は、偽の時計（`FakeTimeProvider`）で拍を手で進めて判定する。**壁時計の間隔を表明しない**。各回の本体の呼び出しが見た偽の時刻が「拍 k 回ぶん」と一致すること、拍を進める前には次の呼び出しが来ないことを表明する | 母集合 A の 5 件 |
| AC-3 | 検出力を保つ: 変異 M1（周期の本体を「失敗なら即座に再試行」の内側のループで包む）を 5 つの常駐処理へ 1 つずつ当てると、当てた常駐処理の試験が赤になる | 変異の記録（下） |
| AC-4 | 母集合 B の対照は、呼び出し側の取り消しを `new TaskCanceledException(…, null, 呼び出し側の token)`（HttpClient・AWS SDK の形）で起こす | 母集合 B の 4 件 |
| AC-5 | 型で判定する変異（絞り込みへ `|| ex is TaskCanceledException` を足す）を母集合 B の 4 つの絞り込みへ 1 つずつ当てると、当てた絞り込みの対照が赤になる | 変異の記録（下） |
| AC-6 | 変更した試験クラスを 20 回連続で実行し、別の試験を並走させて負荷をかけた状態でもすべて合格する | 反復の記録（下） |

## 設計

### 1. 拍の源（AC-1）

- `PeriodicTimer(TimeSpan, TimeProvider)`（.NET 8+）を使う。既定の `TimeProvider.System` は同じ `TimerQueueTimer` を作るので本番の挙動は変わらない。
- 口は既存の `internal CycleInterval`（#1598・#1604）の隣に同じ形で置く: `internal TimeProvider CycleClock { get; init; } = TimeProvider.System;`。
  DI・`Program.cs` は触らない。`RunAsync` へ渡す現在時刻（`DateTimeOffset.UtcNow`）も変えない（射程は拍だけ）。

### 2. 試験の器（AC-2）

- パッケージ `Microsoft.Extensions.TimeProvider.Testing`（`FakeTimeProvider`）を Central Package Management で追加し、3 つの試験プロジェクト
  （DocumentService・GraphService・DataSourceService）だけが参照する。`scripts/backend-library-baseline.json` は不採用ライブラリ（MassTransit）だけの ratchet で、影響しない。
- 器は `FakeTimeProvider` の派生で、`CreateTimer` を上書きして**拍の源が作られたこと**を知らせる。`BackgroundService.StartAsync` は `ExecuteAsync` の完了を
  待たずに返るため、拍の源が作られる前に進めた時刻は拍にならない（偽の時計の拍は作られた時刻から数える）。
- 判定の組み立て（各試験の共通形）:
  1. 拍の源が作られるのを待つ。
  2. 拍 k を進める（`Advance(周期)`）→ 本体の k 回目の呼び出しを上限つきで待つ。呼び出しは**そのとき見た偽の時刻**を記録する。
  3. 失敗した回（k = 1, 2）の後は、短い静穏の窓（実時間 250 ミリ秒）を置いて**呼び出しがまだ k 回である**ことを表明してから次の拍を進める。
  4. 最後に、記録した偽の時刻が「開始 + k × 周期」（DataSource は起動時の 1 回目があるので「開始 + (k−1) × 周期」）と一致することを表明する。
- **正しい実装では決定的である。** 偽の時計は試験が進めない限り進まないので、遅い周期が拍を溜めることは無い。静穏の窓の表明（呼び出しが k 回のまま）は
  拍が無い限り正しい実装では必ず成り立つ —— 窓が長くても短くても**正しい実装を赤にしない**。
- 窓が効くのは変異の側だけである。M1 は失敗の直後に（拍を待たず）本体を呼ぶので、窓の中で k+1 回目が来て赤になる。窓を過ぎても
  次の拍より前に来れば、記録した偽の時刻が前の回と同じになり 4 で赤になる。

### 3. 呼び出し側の取り消しの対照（AC-4）

- DocumentService: `factory.Storage.DeleteThrows` が返す例外を `new TaskCanceledException("…", null, stopping.Token)` にする。
- DataSourceService: `CancellingConnector` を `[Theory]`（探索・取得）にし、`caller.Cancel()` の後 `new TaskCanceledException("…", null, ct)` を投げる。
- McpServer: REST の収集器の対照を足す。127.0.0.1 の何も返さない待受へ本物の HttpClient（期限 30 秒）で収集し、呼び出し側の ct を 300 ミリ秒で取り消す。
  本物の HttpClient が出す形（`TaskCanceledException`）であることを表明に含め、前提の崩れも赤にする。

- DocumentService の対照は、注入の形を変えるだけでは型の変異を殺せなかった（実測）。変異の下でも 2 件目の台帳の逆引き
  （`CollectAsync` の EF 読み）が立った ct で `OperationCanceledException` を投げ直し、「取り消しで終わる」「2 件目が消えない」がどちらも成り立つ。
  そこで **外へ出た例外が注入した取り消しそのもの（`BeSameAs`）であること** と **1 件目を削除の失敗として Error 記録しないこと** で測る
  （器は同じ DB・ストレージで purger を記録用ロガーつきで組む）。#1598 の `PrivateNoteDepartedOwnerPurgeTests` が「照会の回数で測る」としたのと同じ理由である。

## 変異の記録（AC-3・AC-5）

1 か所ずつ当て、該当の試験クラスを走らせ、`git show HEAD:<path> > <path>` で戻した（戻した後 `git status` が空であることを毎回確かめた）。
器: `scratchpad/m1.py`・`tce.py`・`mutate.sh`・`mutate_tce.sh`（作業用。コミットしない）。

### M1（周期の本体の呼び出しを「失敗なら即座に再試行する内側のループ（最大 5 回）」で包む）

| 当てた常駐処理 | 走らせた試験クラス | 結果 | 赤の理由（拍の試験） |
| --- | --- | --- | --- |
| `ClusterDetectionHostedService` | `BatchLoopForeignCancellationTests` | **2/6 赤**（当てた処理の拍の試験と #1598 の試験） | `Expected coordinator.Calls to be 1 … but found 3` |
| `ClusterSummaryHostedService` | 同上 | **2/6 赤**（同上） | 同上 |
| `KnowledgeHealthHostedService` | 同上 | **2/6 赤**（同上） | 同上 |
| `DataSourceSyncHostedService` | `DataSourceSyncHostedServiceTests` | **1/4 赤**（拍の試験） | 同上 |
| `PrivateNoteMaintenanceHostedService` | `PrivateNoteMaintenanceHostedServiceTests` | **2/2 赤** | `Expected askedAt to contain 1 item(s) … but found 3: {01:00:00, 01:00:00, 01:00:00}`（3 回とも同じ拍） |

- 拍の試験は 5 つとも静穏の窓の表明（「失敗の後、拍を進めるまで呼び出しは k 回のまま」）で赤になった。記録した偽の時刻も 3 回とも 1 拍目で、
  窓を過ぎてから再試行する形でも最後の表明（k 回目 = k 拍ぶん）が赤にする。
- #1598 の「次の周期が来る」試験も赤になったのは、今回の M1 が内側の再試行で失敗を記録せずに飲むため（同試験は Error の記録を表明している）。
  #1604 の記録では同試験は M1 の下で緑だった —— M1 の書き方（再試行の前に失敗を記録するか）で変わる副次の検出であり、当てにしない。
  M1 の検出は拍の試験だけで足りている。

### 型で判定する変異（絞り込みへ `|| ex is TaskCanceledException` を足す）

各変異を (1) 本 PR の試験 と (2) 直す前（`origin/develop`）の試験 の両方で走らせた。

| 当てた絞り込み | 走らせた試験 | 本 PR の試験 | 直す前の試験 |
| --- | --- | --- | --- |
| `DocumentObjectPurger.PurgeIsolatedAsync`（L85） | `DeletionPropagationTests` | **赤**（`定期処理は呼び出し側の取り消しを隔離に畳まず伝える`） | 緑（12/12） |
| `DataSourceSyncService` 探索（L95） | `DataSourceSyncServiceTests` | **赤**（`Sync_CallerCancellation_Propagates(onDiscover: True)`） | 緑（15/15） |
| `DataSourceSyncService` 取得（L170） | 同上 | **赤**（`Sync_CallerCancellation_Propagates(onDiscover: False)`） | 緑（15/15。対照が無かった） |
| `HttpToolDeclarationSource.CollectOneAsync`（L71） | `ToolCatalogRefresherTimeoutTests`・`GrpcToolDeclarationCollectorTests`・`ToolDeclarationSource*` | **赤**（`呼び出し側の取り消しは申告なしへ畳まず外へ出す`） | 緑（24/24。対照が無かった） |

## 反復の記録（AC-6）

各クラスを `dotnet test <csproj> --no-build --filter "FullyQualifiedName~<Class>"` で 20 回連続で走らせた（器: `scratchpad/repeat.sh`）。
「負荷あり」は、同時に別の試験スイート（`platform/backend/backend.slnx` 全体と `IngestionService.Tests`）を停止ファイルが置かれるまで交互に
繰り返し走らせた状態である（反復の間に platform 全体 4 回・IngestionService 4 回が完走。いずれも合格）。

| 試験クラス | 負荷なし | 負荷あり |
| --- | --- | --- |
| `PrivateNoteMaintenanceHostedServiceTests` | 20/20 | 20/20 |
| `BatchLoopForeignCancellationTests` | 20/20 | 20/20 |
| `DataSourceSyncHostedServiceTests` | 20/20 | 20/20 |
| `DataSourceSyncServiceTests` | 20/20 | 20/20 |
| `DeletionPropagationTests` | 20/20 | 20/20 |
| `ToolCatalogRefresherTimeoutTests` | 20/20 | 20/20 |

## 結果

- AC-1〜AC-6 を満たした。新しい IADR 番号は取らず、IADR-0299・IADR-0431・IADR-0083・IADR-0462・IADR-0296 へ日付つき追記した。
- フォローアップ（射程外）: `HttpEffectiveConfigCollectorTests.呼び出し側の取り消しは到達不能へ化けずに外へ出る`（共通基盤・#1382）も
  素の `OperationCanceledException` を注入しており、同じ型の変異が生き残り得る。
