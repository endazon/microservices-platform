---
title: IADR-0608 更新イベント（DocumentUpdated）は Transactional Outbox 経由で発行する —— 発行口は「保存と発行を 1 つの関数で受けて確定する」CommitAsync とし（保存の後の DB からイベントを組み立てるため、段 1 の「積む → 確定」の順にしない）、DocumentService の 19 ファイルの全経路を更新の確定と同じトランザクションに載せる（段 2）
type: impl-adr
status: Accepted
related_ids: [FR-06, FR-19, FR-20, ADR-0027, ADR-0061, IADR-0604, IADR-0455, IADR-0447, IADR-0597, IADR-0153]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md（§決定「イベント発行の原子性は Wolverine の EF Core + PostgreSQL Transactional Outbox を標準とする」）
related_specs:
  - ../specs/20261023_2168_transactional-outbox-stage2.md
---

# IADR-0608: 更新イベントは Transactional Outbox 経由で発行する（段 2。#2168）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-23
- 決定者: claude（#2168。親 #2042）

## 起点・関連

- 関連する計画書 ID: FR-06（文書の登録・更新・アーカイブ）、FR-19（個人資料の露出・共有）、FR-20（Obsidian 同期）。
  計画 ADR-0027 §決定（イベント発行の原子性は Wolverine の EF Core + PostgreSQL Transactional Outbox を標準とする）、ADR-0061（発行の門。挙動は変えない）。
- 前提とする実装 ADR: [IADR-0604](./IADR-0604_transactional-outbox-document-deleted.md)（段 1。共通ヘルパ・失敗時の巻き戻し・確定後の送出失敗は Warning・`AllowMultiples`）、
  [IADR-0455](./IADR-0455_publish-gate-all-paths-withdrawal-and-private-note-scope.md)（`DocumentUpdated` の本番経路はすべて門を通す）、
  IADR-0447（共有先の解決点は `ResolveSharedWithAsync` の 1 つ）、IADR-0153（イベントは表示名を運ぶ）、IADR-0597（版の競合は 409）。
- 作業仕様書: [20261023_2168_transactional-outbox-stage2](../specs/20261023_2168_transactional-outbox-stage2.md)。

## コンテキストと課題

`DocumentUpdated` は `IDocumentUpdatedPublisher` 経由で、`SaveChangesAsync` の**後**に素の `IMessageBus.PublishAsync` で出ていた。
段 1 の耐久送出で送出前に Outbox の表へは書かれるが、**DB の確定と同じトランザクションではない**。コミットの後・発行の前に
プロセスが止まると、更新が索引・Wiki・グラフへ伝わらない。

段 1（`IDocumentDeletedPublisher`）の形は「積む（`PublishDeletedAsync`）→ 確定して送る（`SaveChangesAndFlushAsync`）」で、
**発行を保存の前に積む**。`DocumentDeleted` の中身は文書 ID と時刻だけなので、それで足りた。
`DocumentUpdated` は違う —— 中身の一部を**保存の後の DB** から組み立てる:

- 共有先（`sharedWith`）: 門が共有台帳を引く（`ResolveSharedWithAsync`。IADR-0447）。`GrantShare` / `RevokeShare` は台帳の行を足し・消してから再発行する。
- タグの表示名: 門の呼び出し側が辞書を引く（`TagResolver.NamesAsync`）。`Tags/Rename` は改名を保存してから使用中の文書を再発行する。

保存の前に積むと、これらの経路のイベントは**今回の変更が載っていない古い値**を運ぶ。

## 決定

### 決定 1: 発行口は「保存と発行を 1 つの関数で受けて確定する」`CommitAsync` とする

`IDocumentUpdatedPublisher` に `Task CommitAsync(Func<CancellationToken, Task> saveAndPublish, CancellationToken ct)` を足す。
アダプタ（`WolverineDocumentUpdatedPublisher`）は次の順で動く。

1. トランザクションを開く（**保存より前**）。
2. 関数を呼ぶ。関数は `db.SaveChangesAsync` で保存し（まだ確定しない）、保存の後の DB を読んで門へ通し、発行を積む
   （envelope は同じトランザクションへ INSERT される）。
3. コミットする（DB の変更と envelope が一緒に確定する）。
4. 送る。

- **段 1 の「積む → 確定」にしない**理由は上のとおり（イベントが古い値を運ぶ）。トランザクションの中で保存を先に流せば、
  関数の続きは自分の書き込みを読める。結合試験 `保存の後のDBを読んで発行する` が固定する（共有を足した確定のイベントに共有先が載る）。
- **「開く・保存・確定」の 3 段を呼び出し側に書かせない。** 確定の書き忘れは、スコープの破棄でトランザクションが巻き戻り
  **更新そのものが黙って消える**（InMemory の単体試験では見えない）。1 つの関数に閉じれば書き忘れが起きない。
- ポートの引数に DbContext を取らない（Domain のポートが永続化の型に依らない。アダプタはスコープの `IDbContextOutbox<DocumentDbContext>` から得る）。
- 段 1 のポート（`IDocumentDeletedPublisher`）は変えない。中身が保存の後の DB に依らないので、段 1 の形のままで正しい。

### 決定 2: 発行は `CommitAsync` の関数の中でだけ積める

`PublishUpdatedAsync` を `CommitAsync` の外で呼ぶと `InvalidOperationException` にする（入れ子の `CommitAsync` も同じ）。
外で積んだ発行は DB の確定と同じトランザクションに載らず、確定されないまま捨てられる —— 例外にしなければ
「更新は保存されたがイベントが出ない」が黙って起きる。
**`CommitAsync` は DbContext のトランザクションが開いていない状態でだけ呼べる**（開いていれば `InvalidOperationException`）。
外側のトランザクションを持つ呼び出し側から呼ぶと、ここでのコミットが外側を勝手に確定し、巻き戻しは外側の変更まで捨てるためである
（AI レビューの指摘で追加。呼び出し側は外側を持たない約束なので、自分で開いた場合だけ確定する形より例外のほうが単純）。器の代役（`RecordingDocumentUpdatedPublisher`）も同じ例外を投げるので、
経路が確定の外で発行すれば DocumentService.Tests の既存の「更新で DocumentUpdated が出る」試験が赤になる。

失敗の扱いは段 1（IADR-0604 決定 2）と同じである: 確定（保存・コミット）が失敗したら積んだ発行を捨ててトランザクションを巻き戻し、
例外をそのまま出す。確定の**後**の送出の失敗は例外にせず Warning ログにする。`MultiFlushMode.AllowMultiples` で確定する。

### 決定 3: 門に確定込みの入口を置き、19 ファイルの全経路をそこへ寄せる

`DocumentEndpoints` に `SaveChangesAndPublishUpdatedIfIndexableAsync`（単票・複数）と
`SaveChangesAndPublishUpdatedIfIndexableOrWithdrawingAsync` を置く。中身は `CommitAsync(保存 → 表示名辞書 → 門)` で、表示名辞書を返す
（応答の写像に使う）。門（IADR-0455）そのものは変えない。

- 単票の経路（`Archive`・`Publish`・`Create`・`PutBody`・`GrantShare`・`RevokeShare`・`Update`・`UpdateMetadata`・`SetExposure`・
  `SyncConflicts/Resolve`・`ObsidianSync/Push`・`AddDocumentTagUseCase`・`DocumentNormalizedConsumer`）: 保存と発行の 2 行を入口 1 つへ置き換える。
  版の競合（IADR-0597）の 409 への写像は入口の呼び出しを囲む（保存で負ければ発行も巻き戻り 1 通も出ない）。
- 一括の経路（`ArchiveAstQuotes`・`ArchiveBodyless`・`StripUntrustedBoundary`）: ページ全件の保存と発行を 1 回の確定に載せる。
- `Republish`（DB を変えない）と `Tags/Rename`（改名の保存の後に対象を絞って読む）: `CommitAsync` を直接使う。
  `Tags/Rename` の 409（同名の競合）へ写すのは**改名の保存の失敗だけ**である（従前と同じ範囲）。読み出し・発行の積み・コミットの
  失敗は写さずにそのまま出す（AI レビューの指摘で追加。`RenameTagConflictScopeTests` が固定する）。
- **保存の後・発行の前にあった副作用は確定の後ろへ移す**: 計器（`RecordIfUnitSubjectSavedWithoutProject`）、1 件 1 行の監査ログ、
  競合解決の監査、使用量の再計算。確定しなかった変更を数えない・記録しないためである。

### 決定 4: MassTransit の購読（`DocumentNormalizedConsumer`）からの発行も同じ形にする

issue は「MassTransit 側のトランザクションと混ざる」ため扱いを別に決めるとしていた。実測すると、本サービスの `AddMassTransit` は
`UsePlatformRetry` だけで、**MassTransit の Outbox もトランザクションも構成されていない**。DB のトランザクションは Wolverine の 1 つだけで、混ざらない。
よって同じ入口を使う。確定が失敗すれば例外のまま MassTransit の再試行・デッドレターへ出る（従前の保存の失敗と同じ）。
MassTransit の購読を Wolverine へ移すとき（辺 E2）も、この形はそのまま使える。

## 母集合

- `git grep -ln "IDocumentUpdatedPublisher" -- src/knowledge/backend/Services/DocumentService ':!*Tests*'`: 22 ファイル
  （ポート・アダプタ・`Program.cs` ＋ 呼び出し側 19）。19 ファイルの全経路を本 IADR で直した。
- 直した後、門を `CommitAsync` の外から呼ぶ本番経路は 0 件（`git grep -n "PublishUpdatedIfIndexable\(OrWithdrawing\)\?Async(" -- src/knowledge/backend/Services/DocumentService ':!*Tests*'`
  の一致は門の定義・入口の内部・`Republish` と `Tags/Rename` の `CommitAsync` の中だけ）。
- 同型の残り: 他サービスの Wolverine の発行 → 段 3（#2169）。

## 結果

- 良い影響: 更新の確定と `DocumentUpdated` が同じトランザクションで確定する。確定の後に送出が失われても Outbox の表に残り再送される。
  イベントの中身（共有先・タグの表示名）は従前と同じく保存の後の DB から組み立てる。
- 悪い影響 / トレードオフ:
  - ポートの形が段 1 と違う（`SaveChangesAndFlushAsync` と `CommitAsync`）。理由は決定 1。中身が保存の後の DB に依らない段 1 を
    揃えるために書き換える費用（試験の器・呼び出し点）に見合う利点が無いので、揃えない。
  - 関数の中では表示名辞書を読み直す（入口の中で 1 回）。従前と同じ回数である（保存の後に 1 回引いていた）。
  - 到着は少なくとも 1 回（at-least-once）である（段 1 と同じ）。受け手の冪等性は #2101 / #2102 が扱う。
- フォローアップ: 段 3（他サービス）は #2169。

## 検証

- `DocumentUpdatedOutboxTests`（Knowledge.IntegrationTests。実 PostgreSQL ＋ 実 RabbitMQ）4 件:
  確定すると台帳の変更と更新イベントの両方が出る / DB の確定が失敗すれば更新イベントは出ない / 確定の後に送出が失われても再起動後に再送される /
  保存の後の DB を読んで発行する。
- `WolverineDocumentUpdatedPublisherTests`（DocumentService.Tests）3 件: 確定の後の送出の失敗は例外にしない / 確定の外で積むと例外 / 保存が失敗すれば送出しない。
- DocumentService.Tests 全件（器の代役は `CommitAsync` の中でだけ積め、関数が成功した後でだけ記録する）。
