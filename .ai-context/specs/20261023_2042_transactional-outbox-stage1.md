---
title: 作業仕様書 — 文書の削除イベントを Transactional Outbox 経由で発行する（段 1。#2042）
type: spec
status: done
related_ids: [FR-06, FR-19, ADR-0027, ADR-0057, ADR-0096, IADR-0604, IADR-0296, IADR-0314, IADR-0233]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md（§決定「イベント発行の原子性は Wolverine の EF Core + PostgreSQL Transactional Outbox を標準とする」）
  - planning:projects/microservices-platform/07_adr/ADR-0057_deletion-propagation-scope.md（決定 1・3。§結果「再試行または照合の仕組みが要る」）
  - planning:projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md（着手可否の注記。FR-06 は対象外＝留保なし）
---

# 仕様書: 文書の削除イベントを Transactional Outbox 経由で発行する（段 1。#2042）

## 起点となる計画書（トレーサビリティ）

- 要求: FR-06（文書の削除）、FR-19（個人資料の完全削除・90 日経過後の自動物理削除）
- 関連 ADR: ADR-0027（§決定「イベント発行の原子性は Wolverine の EF Core + PostgreSQL Transactional Outbox を標準とする」）、
  ADR-0057 決定 1・3（削除は本文の実体と索引まで及ぶ。整合は「満たすべき条件」）、ADR-0096 決定 1（退職 30 日後の完全削除。射程は ADR-0057 決定 1 と同じ）
- 着手可否: ADR-0037 の注記が留保するのは FR-17〜21 の SC-19 本文編集導線だけで、FR-06 と完全削除の経路は対象外（留保なし）。
- 実装 ADR: IADR-0604（本件の決定）。前提: IADR-0296（オブジェクトが先・DB 行が後）、IADR-0314（発行側の経路宣言）、IADR-0233（共通ヘルパへの封じ込め）

## 目的・背景

DocumentService の削除経路は `SaveChangesAsync` の**後**に素の `IMessageBus.PublishAsync` で `DocumentDeleted` を出していた。
コミットの後・送出の前にブローカ不達かプロセス終了が起きると、台帳の行はもう無く、**`DocumentDeleted` を出し直す起点がどこにも残らない**。
索引（Qdrant）・Wiki・グラフは `DocumentDeleted` でしか撤去しないので、ADR-0057 決定 1 の「残存ゼロ」を構造的に満たせない。
ADR-0027 は Outbox を標準と定めているが、knowledge ユニットにも `Platform.Shared.Infrastructure` にも Outbox は 0 件だった。

## 段の分割

| 段 | 内容 | 扱い |
| --- | --- | --- |
| **段 1（本 PR）** | 共通ヘルパ `UsePlatformTransactionalOutbox` を置き、DocumentService の `DocumentDeleted`（文書の削除・個人資料の完全削除・定期処理の 2 経路）を Outbox 経由にする | 本 PR |
| 段 2 | DocumentService の `DocumentUpdated`（19 ファイル・`IDocumentUpdatedPublisher`）を Outbox 経由にする | 後続 issue |
| 段 3 | 他サービスの Wolverine 発行（DataSourceService の `RawDocumentFetched`、ConversionService の再試行口）を棚卸しし、DB 更新と対になるものを Outbox 経由にする | 後続 issue |

段 2 を分けた理由: 呼び出し点が 19 ファイルにあり、各端点の `SaveChangesAsync` を 1 つずつ置き換える同型の変更になる。
段 1 の形（ポートの 2 メソッド）が確定してから一括で写すほうが差分を読みやすい。
本 PR の `UseDurableOutboxOnAllSendingEndpoints` で `DocumentUpdated` の送出も**耐久化**はされる（ブローカ不達で消えない）が、
DB の確定との原子性は段 2 まで無い。

## 対象範囲

- 対象: `Platform.Shared.Infrastructure`（`WolverineExtensions.UsePlatformTransactionalOutbox`・csproj）、`src/Directory.Packages.props`（2 パッケージ）、
  DocumentService の `IDocumentDeletedPublisher`・`WolverineDocumentDeletedPublisher`・`Program.cs`・削除 3 経路
  （`Documents/Delete/Endpoint.cs`・`PrivateNotes/Purge/Endpoint.cs`・`PrivateNoteMaintenanceService.cs` の 2 メソッド）、試験の器と試験。
- 対象外: `DocumentUpdated`（段 2）、他サービス（段 3）、MassTransit 経路（不採用。触らない）、受け手側の冪等性（#2101 / #2102 が扱う）。

## 母集合（規則 9。誤りの側の文字列で走査）

- `git grep -n "Outbox\|PersistMessagesWith\|UseEntityFrameworkCoreTransactions\|EnlistInTransaction\|IDbContextOutbox" -- 'src/*.cs' 'src/*.props'`（EmailOutbox を除く）: **0 件**（issue の数えを再走査で確認）。
- `git grep -n "\.PublishAsync(\|PublishDeletedAsync(\|PublishUpdatedAsync(\|\.Publish(" -- 'src/knowledge/backend/Services/*.cs' 'src/platform/backend/*.cs' ':!*Tests*'`:
  - `PublishDeletedAsync` の呼び出し 4 箇所（Delete 端点 1・Purge 端点 1・定期処理 2）→ **段 1 の対象（全件）**。
  - `PublishUpdatedAsync` / `IDocumentUpdatedPublisher` を使う 19 ファイル → 段 2。
  - `DataSourceSyncService.cs:179`（`RawDocumentFetched`）・`ConversionJobs/Retry/Endpoint.cs:59` → 段 3（Wolverine の素の発行）。
  - `FigureCorrectionService.cs`・`MassTransitDocumentNormalizedPublisher.cs`・`MassTransitIngestionCompletedPublisher.cs` は MassTransit 経路（Wolverine 移行の射程）で対象外。
  - `NotificationIngress.cs:87` は通知の発行口（Wolverine の `IMessageBus` ではない）で対象外。
- 試験側: `git grep -ln "IDocumentDeletedPublisher\|RecordingMessageBus"` の DocumentService 試験の器は `TestWebApplicationFactory` と `GrpcKestrelFactory` の 2 つ。
  両方に永続化の無効化と発行口の差し替えが要る。

## 設計

1. **共通ヘルパ** `UsePlatformTransactionalOutbox(connectionString)`（`WolverineExtensions`。`UsePlatformMessagingDefaults` と対）:
   `PersistMessagesWithPostgresql(connectionString, "wolverine")`・`UseEntityFrameworkCoreTransactions()`・
   `Policies.UseDurableOutboxOnAllSendingEndpoints()`・`AutoBuildMessageStorageOnStartup = CreateOrUpdate`。
   🔴 接続文字列は **DbContext と同じもの**を渡す（envelope の INSERT は DbContext の接続・トランザクションで走るため、別 DB だと原子性が無い）。
2. **ポート** `IDocumentDeletedPublisher` を 2 メソッドにする: `PublishDeletedAsync`（DB のトランザクションへ積む。送出しない）と
   `SaveChangesAndFlushAsync`（DB の変更と積んだイベントを 1 トランザクションで確定し、確定の後に送出する）。
   失敗時は Outbox を破棄してトランザクションを巻き戻し、例外をそのまま出す（積んだイベントは送られない）。
3. **実装** `WolverineDocumentDeletedPublisher` は `IDbContextOutbox<DocumentDbContext>` を持つ（スコープ内の `DocumentDbContext` と同一インスタンス）。
   1 スコープで複数回確定する定期処理のため `MultiFlushMode.AllowMultiples` で確定する。
4. **削除 3 経路**: 発行を `SaveChangesAsync` の**前**へ移し、`SaveChangesAsync` を `SaveChangesAndFlushAsync` に置き換える。
   「オブジェクトが先・DB 行が後」（IADR-0296 決定 3）は変えない。
5. **Migration**: EF の Migration は足さない。Wolverine の表（`wolverine` スキーマ）は起動時に Wolverine 自身が作る（`AutoBuildMessageStorageOnStartup`）。
   既存の方式（起動時に `MigrateAsync`）と同じく「起動時に適用」で揃う。DB ロールにスキーマ作成の権限が要る（ローカル・PoC は DB の owner なので満たす）。

## 受け入れ基準と試験への写像

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| A1 | DB の確定とイベントが同じトランザクションで確定する（確定すれば台帳の行とイベントの両方が出る。送出後は Outbox の行が残らない） | `DocumentDeletedOutboxTests.確定すると台帳の行と削除イベントの両方が出る`（実 PostgreSQL ＋ 実 RabbitMQ） |
| A2 | DB の確定が失敗すれば、イベントも Outbox の行も残らない | `DocumentDeletedOutboxTests.DBの確定が失敗すれば削除イベントは出ない` |
| A3 | コミットの後・送出の前に止まっても台帳（Outbox の表）に残り、再起動後に再送される | `DocumentDeletedOutboxTests.確定の後に送出が失われても再起動後に再送される` |
| A4 | 共通ヘルパが Outbox の構成（永続化・EF 連携・耐久送出）を入れる | `WolverineExtensionsTests`（Outbox 節） |
| A5 | 削除 3 経路は確定の後にだけイベントを出す（確定しない経路では出さない） | 既存 `DocumentLifecycleEventTests` / `DeletionPropagationTests` / `PrivateNoteDepartedOwnerPurgeTests`（器の発行口が確定時にだけ記録する） |

変異: A2 は「失敗時の巻き戻しを外す」、A1・A3 は「`UseDurableOutboxOnAllSendingEndpoints` を外す」で赤になることを確かめる。

## PoC への反映

- Migration: EF の Migration は無い。DocumentService の起動時に Wolverine が `document_svc` に `wolverine` スキーマと表を作る。
- 順序: DocumentService のイメージを差し替えるだけ（他サービスの順序依存なし）。市場クローズ後に行う。
- 確認: 下の PR 本文に手順を書く。
