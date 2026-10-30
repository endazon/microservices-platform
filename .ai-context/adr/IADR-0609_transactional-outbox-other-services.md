---
title: IADR-0609 他サービスの Wolverine 発行 —— 再変換（ConversionService）の RawDocumentFetched は queued への遷移と同じトランザクションで Outbox 経由に発行し、データソース同期（DataSourceService）の RawDocumentFetched は確定に載せず耐久送出だけにする（段 3）
type: impl-adr
status: Accepted
related_ids: [FR-12, FR-01, UC-04, UC-06, ADR-0027, IADR-0604, IADR-0608, IADR-0245, IADR-0314, IADR-0051]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md（§決定「イベント発行の原子性は Wolverine の EF Core + PostgreSQL Transactional Outbox を標準とする」）
related_specs:
  - ../specs/20261023_2169_transactional-outbox-stage3.md
---

# IADR-0609: 他サービスの Wolverine 発行を Transactional Outbox 経由にする（段 3。#2169）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-23
- 決定者: claude（#2169。親 #2042）

## 起点・関連

- 関連する計画書 ID: FR-12（変換。例外フローの再変換）・UC-06、FR-01（データソース同期）・UC-04。
  計画 ADR-0027 §決定（イベント発行の原子性は Wolverine の EF Core + PostgreSQL Transactional Outbox を標準とする）。
- 前提とする実装 ADR: [IADR-0604](./IADR-0604_transactional-outbox-document-deleted.md)（段 1。共通ヘルパ・失敗時の巻き戻し・確定後の送出失敗は Warning・`AllowMultiples`）、
  [IADR-0608](./IADR-0608_transactional-outbox-document-updated.md)（段 2。`CommitAsync(saveAndPublish)`・外側のトランザクションは例外）、
  IADR-0245（再変換の発行はトポロジ検査から見えない）、IADR-0314（発行側の経路宣言）、IADR-0051（同期の watermark）。
- 作業仕様書: [20261023_2169_transactional-outbox-stage3](../specs/20261023_2169_transactional-outbox-stage3.md)。

## コンテキストと課題

段 1・段 2 で DocumentService の発行は Outbox に載った。残る Wolverine の発行を全サービスで走査すると（母集合は下）、
DocumentService 以外は **`RawDocumentFetched` の 2 箇所**だけである。

- ConversionService の再変換（`/jobs/{id}/retry`）: `PrepareRetryAsync` がジョブを failed → queued にして**保存した後**、素の `IMessageBus.PublishAsync` で出す。
  間でプロセスが止まると、ジョブは queued のまま永久に処理されない。しかも再変換は「失敗以外は 409」なので、**利用者からはやり直せない**。
- DataSourceService の同期: 原本ごとに `PublishAsync` し、ループの**後**に呼び出し側が watermark（`LastSyncedAt`）と失敗計数を保存する。
  永続化が無いので発行はメモリに保持される。ブローカ不達の間にプロセスが止まると発行は消え、それでも watermark は進んでいる
  （保存が先に済んでいれば）—— その原本は増分の対象から外れ、**恒久に欠落する**。

## 決定

### 決定 1: 再変換は queued への遷移と発行を 1 つの確定に載せる（段 2 の形）

ConversionService にポート `IConversionRetryPublisher`（`PublishRetryAsync` ＋ `CommitAsync(saveAndPublish)`）とアダプタ
`WolverineConversionRetryPublisher`（`IDbContextOutbox<ConversionJobDbContext>`）を置き、端点は
`CommitAsync(PrepareRetryAsync → PublishRetryAsync)` で確定する。`Program.cs` で `UsePlatformTransactionalOutbox(connStr)`（DbContext と同じ接続文字列）。

- **段 2 の形（`CommitAsync`）を採る**。`PrepareRetryAsync` は自分で `SaveChangesAsync` する。トランザクションを保存より前に開くこの形なら、
  ストアの約束（遷移して保存し、イベントを返す）を変えずに保存と発行を 1 つの確定に載せられる。段 1 の「積む → 確定」にするには、
  ストアから保存を抜く改変が要る。
- 失敗の扱い・外側のトランザクションの拒否・確定の外で積めない門番・`AllowMultiples` は段 2（IADR-0608 決定 2）と同じ。
  確定の後の送出の失敗は Warning にする —— 例外にすると確定済みの再変換に 500 を返し、やり直すと 409 になる。
- ポートを Domain に置き、発行は別ファイルのアダプタに置く（MassTransit の発行口 `MassTransitDocumentNormalizedPublisher` と 1 ファイル 1 トランスポートを保つ）。
  再変換の発行は従前どおりトポロジ検査から見えない（IADR-0245 決定 6-2。イベントはストアが組み立てる）。

### 決定 2: データソース同期は確定に載せず、耐久送出だけにする

DataSourceService は `Program.cs` で `UsePlatformTransactionalOutbox(connStr)` を呼び、発行箇所は `IMessageBus.PublishAsync` のままとする。

- 同期の発行と watermark の保存は**対にしない**。要る不変条件は「watermark を進めた時点で、それまでの発行が失われていない」の一方向だけである。
  耐久送出では `PublishAsync` が送出の前に Outbox の表へ書く（WolverineFx 6.24.4 の `DurableSendingAgent.storeAndForwardAsync` →
  `StoreOutgoingAsync` → 送出キュー。逆コンパイルで確認）ので、保存より前に発行は表に残る。逆向き（発行済み・保存前に停止）は watermark が
  進まず次回に再発行され、決定的な DocumentId で下流が冪等に upsert する（従前からの約束。`DataSourceSyncService.SyncAsync` の注記）。
- **確定に載せる案は採らない。** 発行を `IDbContextOutbox` に積むと、トランザクションが最初の発行で開いたまま、コネクタの取得と
  オブジェクトストレージへの格納を含む同期全体（定期同期では全ソース）にわたって保たれる。下流への発行もすべて同期の終わりまで遅れる。
  得られるのは下の残余リスクの解消だけで、費用に見合わない。

## 母集合

- 素の発行: `git grep -nE "\b(PublishAsync|SendAsync|InvokeAsync|ScheduleAsync|BroadcastToTopicAsync|SendToEndpointAsync)\(" -- 'src/*/backend/Services/*.cs' ':!*Tests*' ':!*/Tests/*'`
  → Wolverine の発行 4 件（DocumentService 2〔段 1・2 で済〕・ConversionService 1・DataSourceService 1）。同名の HTTP・SMTP・MCP のメソッドは除外。
- バスの注入（`IMessageBus|IMessageContext|OutgoingMessages|IDbContextOutbox|ICommandBus`）: 同じ 4 ファイル。
- カスケード: Wolverine の購読 8 クラスの `Handle` はすべて `Task` を返す（0 件）。platform のサービスは `UseWolverine` 0 件。
- 対象外: MassTransit の発行（Wolverine 移行の射程）。

## 結果

- 良い影響: Wolverine の発行はすべて耐久になった（DocumentService・ConversionService・DataSourceService）。DB 更新と対になる発行
  （削除・更新・再変換）は確定と同じトランザクションで確定する。
- 悪い影響 / トレードオフ:
  - ConversionService・DataSourceService の発行が送出前に 1 回 DB へ書く。両サービスの DB に `wolverine` スキーマが起動時に作られる（DB ロールにスキーマ作成の権限が要る。ローカル・PoC は owner の `kp`）。
  - ConversionService は購読（`RawDocumentFetchedConsumer`）も持つ。耐久化は送出先だけに効き、購読の受け口（Buffered）は変えない。
  - 🔴 **残余リスク（決定 2）**: 表への書き込み（`StoreOutgoingAsync`）が一時的に失敗すると、Wolverine は例外を出さずにメモリ上で書き込みを
    再試行する（JasperFx の `RetryBlock`。逆コンパイルで確認）。その間に watermark の保存が成功し、再試行が済む前にプロセスが止まると、
    その原本は欠落し得る。同じ DB への書き込みが「発行だけ失敗し、直後の保存は成功する」窓に限られる。運用で気づく手段は、
    Wolverine のエラーログ（`Error while trying to retry`）である。
  - 到着は少なくとも 1 回（at-least-once）。再変換の再送で同じ `RawDocumentFetched` が 2 度届き得る（受け手の冪等性は #2101 / #2102 が扱う）。
- フォローアップ: 決定 2 の残余リスク（表への書き込みの一時失敗）の運用でのアラート化は #2178 で扱う（AI レビューの指摘）。
  ［2026-10-30 追記 / #2178］アラート化した。ログのテンプレートの照合で計器 `messaging.retry_block.errors` にし、警報 `MessagingRetryBlockErrors` で鳴らす（[IADR-0656](./IADR-0656_retry-block-error-metric-instead-of-loki-rule.md)。Loki の ruler は未配備のため新設せず、計器化を選んだ）。
  #2042 の Wolverine の発行はこれで全件である。MassTransit の残りの辺は Wolverine 移行の射程。
- 注意（AI レビューの指摘で追記）: 発行口の `CommitAsync` はトランザクションを `BeginTransactionAsync` で直接開く。いま `ConversionJobDbContext`
  （と DocumentService の同形）は実行戦略（`EnableRetryOnFailure` 等）を有効にしていない（`git grep -nE "EnableRetryOnFailure|ExecutionStrategy" -- src` が 0 件）。
  実行戦略を有効化する場合は、確定の全体を `CreateExecutionStrategy().ExecuteAsync` で包む必要がある（包まないとユーザー開始のトランザクションが例外になる）。

## 検証

- `ConversionRetryOutboxTests`（Knowledge.IntegrationTests。実 PostgreSQL ＋ 実 RabbitMQ）4 件: 確定でジョブ queued とイベント /
  確定が失敗すればイベントは出ずジョブは failed のまま / 確定の後に送出が失われても再起動後に再送 / 外側のトランザクションでは例外。
- `RawDocumentFetchedDurableSendTests`（同）: DataSourceService の本番の配線で送出先が耐久である。
- `WolverineConversionRetryPublisherTests`（ConversionService.Tests）3 件。ConversionService.Tests・DataSourceService.Tests 全件。
- 変異: `ClearAllAsync` を外す → 確定失敗の試験が赤。DataSourceService の `UsePlatformTransactionalOutbox` を外す → 耐久の試験が赤。
  門番・送出失敗の catch を外す → 単体試験が赤。
