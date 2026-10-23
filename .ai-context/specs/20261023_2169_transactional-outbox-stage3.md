---
title: 作業仕様書 — 他サービスの Wolverine 発行を棚卸しし、DB 更新と対になるものを Transactional Outbox 経由にする（段 3。#2169 / #2042）
type: spec
status: done
related_ids: [FR-12, FR-01, UC-04, UC-06, ADR-0027, IADR-0609, IADR-0604, IADR-0608, IADR-0245, IADR-0314]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md（§決定「イベント発行の原子性は Wolverine の EF Core + PostgreSQL Transactional Outbox を標準とする」）
---

# 仕様書: 他サービスの Wolverine 発行を Transactional Outbox 経由にする（段 3。#2169）

## 起点となる計画書（トレーサビリティ）

- 要求: FR-12（変換。例外フローの再変換）・UC-06、FR-01（データソース同期）・UC-04
- 関連 ADR: ADR-0027 §決定（Transactional Outbox を標準とする）
- 実装 ADR: IADR-0609（本件の決定）。前提: IADR-0604（段 1。共通ヘルパ・失敗時の巻き戻し・確定後の送出失敗は Warning・`AllowMultiples`）、
  IADR-0608（段 2。`CommitAsync(saveAndPublish)` の形）、IADR-0245（再変換の発行はトポロジ検査から見えない）、IADR-0314（発行側の経路宣言）
- issue: #2169（親 #2042）。

## 母集合（規則 9・10。誤りの側の文字列で全サービスを走査）

1. 素の発行: `git grep -nE "\b(PublishAsync|SendAsync|InvokeAsync|ScheduleAsync|BroadcastToTopicAsync|SendToEndpointAsync)\(" -- 'src/*/backend/Services/*.cs' ':!*Tests*' ':!*/Tests/*'`
   → Wolverine の発行は 4 件（HTTP・SMTP・MCP の同名メソッドは除外）。
2. バスの注入: `git grep -nE "IMessageBus|IMessageContext|OutgoingMessages|IDbContextOutbox|ICommandBus" -- 'src/*.cs' ':!*Tests*'` → 上と同じ 4 ファイル（＋ DocumentService のポートの注記）。
3. カスケード（ハンドラの戻り値で出す発行）: Wolverine の購読 8 クラスの `Handle` はすべて `Task` を返す（戻り値の発行は 0 件）。
4. platform ユニットのサービス（AuthorizationService・LlmGateway・McpServer・NotificationService）は Wolverine を使わない（`UseWolverine` 0 件）。
   NotificationService の `EmailOutbox` は自前のメール送出表で、Wolverine の発行ではない。

| # | 発行 | サービス | 対になる DB 更新 | 判定 | 扱い |
| --- | --- | --- | --- | --- | --- |
| 1 | `DocumentDeleted`（`WolverineDocumentDeletedPublisher`） | DocumentService | 台帳の削除 | 対になる | 段 1（IADR-0604）で済 |
| 2 | `DocumentUpdated`（`WolverineDocumentUpdatedPublisher`） | DocumentService | 文書の更新 | 対になる | 段 2（IADR-0608）で済 |
| 3 | `RawDocumentFetched`（`ConversionJobs/Retry/Endpoint.cs`。再変換） | ConversionService | ジョブを failed → queued（`PrepareRetryAsync` が保存） | **対になる**: 保存の後・発行の前に止まると、ジョブは queued のまま永久に処理されず、再変換は「失敗以外は 409」で受け付けない（利用者から取り返しが付かない） | **本 PR**: `IConversionRetryPublisher.CommitAsync` で遷移と発行を 1 つの確定に載せる |
| 4 | `RawDocumentFetched`（`DataSources/Sync/DataSourceSyncService.cs`。同期） | DataSourceService | watermark（`LastSyncedAt`）と失敗計数。保存は呼び出し側がループの**後**に 1 回 | **対にしない**: 発行は保存より**前**で、逆向き（発行済み・未保存）は決定的な DocumentId で冪等に再発行される。要るのは「watermark を進めた時点で発行が失われていない」ことだけで、耐久送出で満たせる | **本 PR**: `UsePlatformTransactionalOutbox` を呼んで送出先を耐久にする（発行は確定に載せない） |

- 除外: MassTransit の発行（`MassTransitDocumentNormalizedPublisher`・`FigureCorrectionService`・`MassTransitIngestionCompletedPublisher`）は Wolverine 移行の射程（issue の指示どおり）。
- 規模: 2 サービス・約 20 ファイルで目安（40 ファイル）を下回るため、サービス単位に分割しない。

## 受け入れ基準

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| C1 | 再変換の確定で、ジョブは queued になり `RawDocumentFetched` が出る（送出後は Outbox の行が残らない） | `ConversionRetryOutboxTests.確定するとジョブはqueuedになり再変換のイベントが出る`（実 PostgreSQL ＋ 実 RabbitMQ） |
| C2 | 確定が失敗すればジョブは failed のままでイベントも Outbox の行も出ない（同じスコープの後続の確定にも混ざらない） | `ConversionRetryOutboxTests.DBの確定が失敗すれば再変換のイベントは出ずジョブはfailedのまま` |
| C3 | 確定の後に送出が失われても Outbox の表に残り、再起動したホストが再送する | `ConversionRetryOutboxTests.確定の後に送出が失われても再起動後に再送される` |
| C4 | 外側のトランザクションが開いていれば確定せず例外にする | `ConversionRetryOutboxTests.外側のトランザクションの中では確定せず例外にする` |
| C5 | 確定の後の送出の失敗は例外にせず Warning、確定の外で積むと例外、保存の失敗では送出しない | `WolverineConversionRetryPublisherTests`（3 件） |
| C6 | 再変換の端点は確定の中で発行する（器の代役は `CommitAsync` の外で積むと例外） | 既存 `ConversionJobEndpointTests.Retry_KnownFailedJob_Returns202` ほか ConversionService.Tests 全件 |
| D1 | DataSourceService の本番の配線で、`RawDocumentFetched` の送出先は耐久である | `RawDocumentFetchedDurableSendTests.同期の原本イベントの送出先は耐久である`（実 PostgreSQL ＋ 実 RabbitMQ） |

変異（実測）: C2 は「失敗時の `ClearAllAsync` を外す」、D1 は「DataSourceService の `Program.cs` から `UsePlatformTransactionalOutbox` を外す」、
C5 は「門番を外す」「送出の失敗の catch を外す」で、それぞれ赤になった。

## 設計

1. ConversionService: ポート `IConversionRetryPublisher`（`PublishRetryAsync` ＋ `CommitAsync(saveAndPublish)`）とアダプタ
   `WolverineConversionRetryPublisher`（`IDbContextOutbox<ConversionJobDbContext>`）。形と失敗の扱いは段 2 と同じ。
   段 2 の形を採るのは、`PrepareRetryAsync` が自分で保存するため（トランザクションを保存より前に開けば、ストアの約束を変えずに済む）。
   `Program.cs` で `UsePlatformTransactionalOutbox(connStr)`（DbContext と同じ接続文字列）。
2. DataSourceService: `Program.cs` で `UsePlatformTransactionalOutbox(connStr)` だけを呼ぶ。発行箇所は `IMessageBus.PublishAsync` のまま。
3. 試験の器: InMemory の器は `DisableAllWolverineMessagePersistence()` を足し、再変換を叩く器は発行口を代役
   `RecordingConversionRetryPublisher` へ差し替える。

## 対象範囲

- 対象: 上の 2 サービスの `Program.cs`・ポート・アダプタ・端点・器と試験、`Knowledge.IntegrationTests` の結合試験 2 クラス、
  IADR-0609、`docs/tech/tech-requirements.md`（適用範囲の記述）。
- 対象外: 共通ヘルパ（段 1 のまま）、DocumentService、MassTransit の発行、受け手の冪等性（#2101 / #2102）。

## PoC への反映

- Migration なし。ConversionService・DataSourceService の初回起動で、それぞれの DB（`conversion_svc`・`datasource_svc`）に
  `wolverine` スキーマと表が作られる（DB の owner は `kp` なので権限は足りる）。イメージの差し替えだけで、市場クローズ後に行う。
