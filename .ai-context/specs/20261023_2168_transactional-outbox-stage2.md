---
title: 作業仕様書 — 文書の更新イベントを Transactional Outbox 経由で発行する（段 2。#2168 / #2042）
type: spec
status: done
related_ids: [FR-06, FR-19, FR-20, ADR-0027, ADR-0061, IADR-0608, IADR-0604, IADR-0455, IADR-0447, IADR-0597]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md（§決定「イベント発行の原子性は Wolverine の EF Core + PostgreSQL Transactional Outbox を標準とする」）
  - planning:projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md（着手可否の注記。本件は発行の確定境界だけを動かし、SC-19 本文編集導線には触れない）
---

# 仕様書: 文書の更新イベントを Transactional Outbox 経由で発行する（段 2。#2168）

## 起点となる計画書（トレーサビリティ）

- 要求: FR-06（文書の登録・更新・アーカイブ）、FR-19（個人資料の露出・共有）、FR-20（Obsidian 同期の push・競合解決）
- 関連 ADR: ADR-0027 §決定（Transactional Outbox を標準とする）、ADR-0061（発行の門。挙動は変えない）
- 着手可否: ADR-0037 の注記が留保するのは SC-19 の本文編集導線だけである。本件は既存経路の確定の境界を動かすだけで、導線を足さない。
- 実装 ADR: IADR-0608（本件の決定）。前提: IADR-0604（段 1。共通ヘルパ・失敗時の巻き戻し・確定後の送出失敗は Warning）、
  IADR-0455（発行の門）、IADR-0447（共有先の解決点は 1 つ）、IADR-0597（版の競合は 409）
- issue: #2168（親 #2042）。段 3（他サービス）は #2169 で、本 PR に含めない。

## 目的・背景

`DocumentUpdated` は `IDocumentUpdatedPublisher` 経由で、`SaveChangesAsync` の**後**に素の `IMessageBus.PublishAsync` で出ていた。
段 1 の `UseDurableOutboxOnAllSendingEndpoints` で送出前に Outbox の表へは書かれるが、**DB の確定と同じトランザクションではない**。
コミットの後・発行の前にプロセスが止まると、更新が索引・Wiki・グラフへ伝わらない。

## 受け入れ基準（issue #2168 から写す）

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| B1 | 更新の確定とイベントが同じトランザクションで確定する（確定すれば台帳の変更とイベントの両方が出る。送出後は Outbox の行が残らない） | `DocumentUpdatedOutboxTests.確定すると台帳の変更と更新イベントの両方が出る`（実 PostgreSQL ＋ 実 RabbitMQ） |
| B2 | 更新の確定が失敗すれば `DocumentUpdated` は出ない（Outbox の行も残らず、同じスコープの後続の確定にも混ざらない） | `DocumentUpdatedOutboxTests.DBの確定が失敗すれば更新イベントは出ない` |
| B3 | 確定の後に送出が失われても Outbox の表に残り、再起動したホストが再送する | `DocumentUpdatedOutboxTests.確定の後に送出が失われても再起動後に再送される` |
| B4 | イベントの中身は保存の**後**の DB から組み立てる（共有の付与・取り消しの直後の `sharedWith` に今回の変更が載る） | 既存 `DocumentShareEndpointTests` 系（器の発行口）＋ `DocumentUpdatedOutboxTests.保存の後のDBを読んで発行する`（実 PostgreSQL） |
| B5 | 確定の後の送出の失敗は呼び出し側へ出さず Warning で残し、確定済みの更新を成功として返す | `WolverineDocumentUpdatedPublisherTests.確定の後の送出の失敗は例外にせずWarningで残し更新は確定している` |
| B6 | `CommitAsync` の外で発行を積むと例外になる（確定されないまま黙って捨てられる発行を作らない） | `WolverineDocumentUpdatedPublisherTests.確定の外で発行を積むと例外になる` ／ 器の発行口も同じ例外を投げる |
| B7 | 19 ファイルの全経路が確定を `CommitAsync` で行う（器の発行口は `CommitAsync` の中でだけ記録するので、確定の外で発行すれば既存の「更新で DocumentUpdated が出る」試験が赤になる） | 既存の DocumentService.Tests 全件 |

変異（実測）: B2 は「失敗時の巻き戻しを外す」、B4 は「入口の中で保存を発行の後ろへ動かす」（結合 1 件＋ DocumentService.Tests の共有先の 2 件）、
B5 は「送出の失敗の catch を外す」、B6 は「門番を外す」、B7 は「`Archive` を従前の『保存 → 門』へ戻す」（DocumentService.Tests 6 件）で、それぞれ赤になった。

## 母集合（規則 9。誤りの側の文字列で走査）

- `git grep -ln "IDocumentUpdatedPublisher" -- src/knowledge/backend/Services/DocumentService ':!*Tests*'`: 22 ファイル
  ＝ポート 1・アダプタ 1・`Program.cs` 1 ＋ **呼び出し側 19**（issue の数え 19 を再走査で確認）。
  - 端点 16: Documents の `Archive`・`ArchiveAstQuotes`・`ArchiveBodyless`・`Create`・`GrantShare`・`Publish`・`PutBody`・`Republish`・
    `RevokeShare`・`StripUntrustedBoundary`・`Update`・`UpdateMetadata`、`ObsidianSync/Push`、`PrivateNotes/SetExposure`、
    `SyncConflicts/Resolve`、`Tags/Rename`
  - `AddDocumentTagUseCase`・`DocumentNormalizedConsumer`（MassTransit の購読）・`DocumentEndpoints`（門の実装）
- 試験側: `git grep -ln "IDocumentUpdatedPublisher" -- src/knowledge/backend/Services/DocumentService/Tests src/knowledge/backend/Tests`:
  Catalog の偽の発行口 6 ファイル＋ `PublishGateCoverageTests`。器（`TestWebApplicationFactory`・`GrpcKestrelFactory`）は
  本番のアダプタを `RecordingMessageBus` と組み合わせて使っていたため、代役へ差し替えが要る（段 1 と同じ）。
- 除外: 他サービスの発行（段 3・#2169）。MassTransit の発行（移行の射程）。

## 設計

1. **ポートの形を「保存と発行を 1 つの関数で渡す」にする**（IADR-0608 決定 1）:
   `Task CommitAsync(Func<CancellationToken, Task> saveAndPublish, CancellationToken ct)`。
   アダプタはトランザクションを開き、`saveAndPublish`（中で `db.SaveChangesAsync` → 発行を積む）を呼び、コミットし、送る。
   - 段 1 の「積む → `SaveChangesAndFlushAsync`」の順にしない理由: `DocumentUpdated` の中身は**保存の後の DB** から組み立てる
     （共有先 `ResolveSharedWithAsync`・タグの表示名 `TagResolver.NamesAsync`）。保存の前に積むと、共有の付与・取り消し・タグ改名の
     直後のイベントが古い値を運ぶ。トランザクションの中で保存を先に流せば、自分の書き込みを読める。
   - 3 段（開く・保存・確定）を呼び出し側に書かせない理由: 確定を書き忘れると、スコープの破棄でトランザクションが巻き戻り
     **更新そのものが黙って消える**。1 つの関数に閉じれば書き忘れが起きない。
2. **`PublishUpdatedAsync` は `CommitAsync` の中でだけ呼べる**（外では `InvalidOperationException`）。積んだまま確定されない発行を作らない。
3. **失敗の扱いは段 1 と同じ**: 確定（保存・コミット）の失敗は積んだ発行を捨てて巻き戻し、例外をそのまま出す。確定の後の送出の失敗は Warning。
   `MultiFlushMode.AllowMultiples`（1 スコープで複数回確定してよい）。
4. **門（`DocumentEndpoints`）に確定込みの入口を足す**: `SaveChangesAndPublishUpdatedIfIndexableAsync`（単票・複数）と
   `SaveChangesAndPublishUpdatedIfIndexableOrWithdrawingAsync`。表示名辞書を返す（応答の写像に使う）。
5. **各経路**: 保存と発行を上の入口へ置き換える。保存の後・発行の前にあった副作用（計器・監査・使用量の再計算・1 件 1 行の監査ログ）は
   確定の**後**へ移す（確定しなかった変更を数えない・記録しない）。
6. **`DocumentNormalizedConsumer`（MassTransit の購読）も同じ形にする**（IADR-0608 決定 4）。DocumentService の MassTransit には
   Outbox もトランザクションも構成されていない（`AddMassTransit` は `UsePlatformRetry` だけ）ので、DB のトランザクションは
   Wolverine の 1 つだけになり混ざらない。確定の失敗は例外のまま MassTransit の再試行へ出る（従前の保存失敗と同じ）。
7. 試験の器: `RecordingDocumentUpdatedPublisher`（`CommitAsync` の中で積み、関数が成功した後でだけ `RecordingMessageBus` へ出す）。
   Catalog の偽の発行口は `CommitAsync` を「関数を呼ぶだけ」で実装する。

## 対象範囲

- 対象: 上の 19 ファイル・ポート・アダプタ・器と試験・`Knowledge.IntegrationTests` に結合試験・IADR-0608・`docs/tech/tech-requirements.md`（適用範囲の記述）。
- 対象外: 共通ヘルパ（段 1 のまま）、`IDocumentDeletedPublisher`（段 1 の形のまま）、他サービス（段 3）、受け手の冪等性（#2101 / #2102）。

## PoC への反映

- Migration なし（Outbox の表は段 1 で作られている）。DocumentService のイメージ差し替えだけ。市場クローズ後に行う。
- 確認手順は PR 本文に書く。
