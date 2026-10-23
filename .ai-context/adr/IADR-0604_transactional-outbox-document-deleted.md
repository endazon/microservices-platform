---
title: IADR-0604 削除イベント（DocumentDeleted）は Transactional Outbox 経由で発行する —— 共通ヘルパ UsePlatformTransactionalOutbox（PostgreSQL 永続化・EF 連携・全送出先の耐久化）を置き、DocumentService の削除 3 経路を削除の確定と同じトランザクションに載せる（段 1）
type: impl-adr
status: Accepted
related_ids: [FR-06, FR-19, ADR-0027, ADR-0057, ADR-0096, IADR-0296, IADR-0314, IADR-0233, IADR-0597]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md（§決定「イベント発行の原子性は Wolverine の EF Core + PostgreSQL Transactional Outbox を標準とする」）
  - planning:projects/microservices-platform/07_adr/ADR-0057_deletion-propagation-scope.md（決定 1・3）
  - planning:projects/microservices-platform/07_adr/ADR-0096_private-note-disposal-after-view-window.md（決定 1）
related_specs:
  - ../specs/20261023_2042_transactional-outbox-stage1.md
---

# IADR-0604: 削除イベントは Transactional Outbox 経由で発行する（段 1。#2042）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-23
- 決定者: claude（#2042。親 #2047 の機能面の全体監査）

## 起点・関連

- 関連する計画書 ID: FR-06（文書の削除）、FR-19（個人資料の完全削除・90 日経過後の自動物理削除）。
  計画 ADR-0027 §決定（イベント発行の原子性は Wolverine の EF Core + PostgreSQL Transactional Outbox を標準とする）、
  ADR-0057 決定 1・3（削除は本文の実体と索引まで及ぶ。整合は満たすべき条件）、ADR-0096 決定 1（退職 30 日後の完全削除。射程は ADR-0057 決定 1 と同じ）。
- 前提とする実装 ADR: [IADR-0296](./IADR-0296_deletion-propagation-to-object-storage.md)（オブジェクトが先・DB 行が後。本 IADR は変えない）、
  [IADR-0314](./IADR-0314_wolverine-outbound-routing-and-queue-binding.md)（発行側の経路宣言）、[IADR-0233](./IADR-0233_wolverine-shared-helper-confinement.md)（Wolverine の構成は共通ヘルパへ封じる）、
  [IADR-0597](./IADR-0597_document-version-optimistic-concurrency.md)（削除の版の競合は 409）。
- 作業仕様書: [20261023_2042_transactional-outbox-stage1](../specs/20261023_2042_transactional-outbox-stage1.md)。

## コンテキストと課題

DocumentService の削除 3 経路（組織文書の削除・個人資料の完全削除・定期処理〔90 日の自動物理削除と退職 30 日後の完全削除〕）は、
`SaveChangesAsync` の**後**に素の `IMessageBus.PublishAsync` で `DocumentDeleted` を出していた。
コミットの後・送出の前にブローカ不達かプロセス終了が起きると、台帳の行はもう無く、**イベントを出し直す起点がどこにも残らない**。
索引・Wiki・グラフは `DocumentDeleted` でしか撤去しないため、ADR-0057 決定 1 の「残存ゼロ」を構造的に満たせなかった。
IADR-0296 決定 3 はストレージ対 DB の壊れ方だけを比べており、DB 対イベントは未検討だった。
ADR-0027 は Outbox を標準と定めるが、knowledge ユニットにも `Platform.Shared.Infrastructure` にも Outbox の構成は 0 件だった。

## 決定

### 決定 1: Outbox の構成は共通ヘルパ `UsePlatformTransactionalOutbox(connectionString)` へ封じる

`WolverineExtensions` に置き、`UsePlatformMessagingDefaults` と対で呼ぶ。中身は次の 4 つである。

| 設定 | 欠けたときの壊れ方 |
| --- | --- |
| `PersistMessagesWithPostgresql(cs, "wolverine")` | `IDbContextOutbox` が最初の発行で「永続化が無い」と投げる |
| `UseEntityFrameworkCoreTransactions()` | `IDbContextOutbox<T>` が DI に無い |
| `Policies.UseDurableOutboxOnAllSendingEndpoints()` | 🔴 **発行が Outbox の表へ書かれない**（Wolverine は送出先が耐久のときだけ envelope を永続化する）。起動もビルドも通り、障害時にだけ消える |
| `AutoBuildMessageStorageOnStartup = CreateOrUpdate` | 既定は JasperFx のプロファイル依存。None なら表が作られず最初の発行で落ちる |

- 🔴 **接続文字列は発行と対になる DbContext と同じ DB を渡す。** DbContext のモデルに Wolverine の表を写さない構成では、
  envelope の INSERT は **DbContext の接続とトランザクションで**走る（`EfCoreEnvelopeTransaction`）。別 DB では原子性が無い。
- 表は `wolverine` スキーマに置く（業務表と分ける）。DB はサービスごとに別なので、スキーマ名は全サービス共通でよい。
- **耐久化は全送出先に効く。** DB と対にならない発行（DocumentService の `DocumentUpdated`。段 2 で Outbox 化する）も、
  送出前に Outbox の表へ書かれるので、ブローカ不達では消えなくなる。DB の確定との原子性は段 2 まで無い。

### 決定 2: 発行口のポートを「積む」と「確定して送る」の 2 メソッドにする

`IDocumentDeletedPublisher` に `SaveChangesAndFlushAsync` を足す。呼び出し側の約束は次のとおりである。

1. DB の変更を DbContext へ積み、`PublishDeletedAsync` で発行も積む（送らない）。
2. `SaveChangesAndFlushAsync` で DB の変更と積んだ発行を **1 トランザクションで**確定し、確定の後に送る。

- 実装（`WolverineDocumentDeletedPublisher`）は `IDbContextOutbox<DocumentDbContext>` を持つ。DI のスコープで解決されるので、
  端点が受け取る `DocumentDbContext` と同一インスタンスである。
- `MultiFlushMode.AllowMultiples` で確定する。既定の `OnlyOnce` は 2 回目以降の送出を**警告 1 行で黙って捨てる**
  （envelope は表に残るが送出ノードの所有のままで、再送の対象にならない）。定期処理は 1 スコープで所有者ごと・段ごとに確定する。
- 🔴 **確定が失敗したら、積んだ発行を捨ててトランザクションを巻き戻してから例外を出す。** 巻き戻さないと、確定しなかった削除の
  イベントが同じスコープの後続の確定と一緒に送られる（**実測**。EF はユーザーのトランザクション内の `SaveChanges` の前に
  セーブポイントを置き、失敗してもそこまでしか戻さない。envelope の INSERT はセーブポイントより前なので生き残る）。
- 🔴 **確定の後の送出の失敗は呼び出し側へ出さず、Warning ログにして成功として返す。** 削除は確定済みで、送れなかったイベントは
  Outbox の表に残り再送される。例外にすると確定済みの削除に 500 を返し、やり直すと 404 になる（AI レビューの指摘で追記。
  `WolverineDocumentDeletedPublisherTests` が固定し、例外を素通しにする変異で赤になる）。確定（コミット）までの失敗は例外のままである。
- ポート名は変えない（呼び出し点・試験・トポロジ検査の参照を動かさない）。発行（`Publish(new DocumentDeleted(`）は引き続き
  アダプタのファイルにあり、`check-event-topology.js` の検出は変わらない。

### 決定 3: 削除 3 経路は発行を確定の前へ移し、確定を `SaveChangesAndFlushAsync` で行う

- 組織文書の削除: 発行 → 確定。版の競合（IADR-0597）で負ければ発行も巻き戻り、409 を返す。
- 個人資料の完全削除: 行の削除と発行を 1 回の確定に載せる。使用量の再計算（2 回目の保存）は確定の後のまま。
- 定期処理（90 日・退職）: 同じ。事後通知と使用量の再計算は確定の後のまま。
- 「オブジェクトが先・DB 行が後」（IADR-0296 決定 3）は変えない。

### 決定 4: Wolverine の表は EF の Migration に入れず、起動時に Wolverine が作る

- `MapWolverineEnvelopeStorage` で DbContext のモデルへ写す案は採らない。写せるのは envelope の 2 表だけで、ノード・
  デッドレター等の残りは結局 Wolverine が起動時に作る。**2 つの仕組みが同じスキーマを管理する**形になり、版更新で食い違う。
- 既存の方式（起動時に `MigrateAsync`）と同じく「起動時に適用」で揃う。DB ロールにスキーマ作成の権限が要る（ローカル・PoC は DB の owner）。

## 母集合

- `git grep -n "Outbox\|PersistMessagesWith\|UseEntityFrameworkCoreTransactions\|EnlistInTransaction\|IDbContextOutbox" -- 'src/*.cs' 'src/*.props'`（EmailOutbox を除く）: 着手前 0 件。
- `PublishDeletedAsync` の呼び出し 4 箇所（端点 2・定期処理 2）→ 全件を本 IADR で直した。
- 同型の残り: DocumentService の `DocumentUpdated`（`IDocumentUpdatedPublisher` を使う 19 ファイル）→ 段 2。
  DataSourceService の `RawDocumentFetched`・ConversionService の再試行口（Wolverine の素の発行）→ 段 3。MassTransit 経路は対象外（移行の射程）。

## 結果

- 良い影響: 削除の確定と `DocumentDeleted` が同じトランザクションで確定する。確定の後に送出が失われても Outbox の表に残り、
  耐久エージェントが送る（正常停止なら次に起きたノードが即座に、異常終了ならノードの死活判定〔既定 1 分〕の後に）。
- 悪い影響 / トレードオフ:
  - `Platform.Shared.Infrastructure` が EF Core 連携と PostgreSQL 永続化のパッケージを参照する（全サービスへ推移する）。Outbox の構成を
    共通ヘルパへ封じるための費用として受ける（IADR-0233 と同じ判断）。
  - DocumentService の全発行（`DocumentUpdated` を含む）が送出前に 1 回 DB へ書く。
  - 到着は少なくとも 1 回（at-least-once）である。再送で同じ `DocumentDeleted` が 2 度届き得る。受け手の撤去は冪等（既に無ければ何もしない）なので受ける。
- フォローアップ: 段 2（`DocumentUpdated`）は #2168、段 3（他サービス）は #2169 で追う。

## 検証

- `DocumentDeletedOutboxTests`（Knowledge.IntegrationTests。実 PostgreSQL ＋ 実 RabbitMQ）3 件:
  確定すると台帳の行とイベントの両方が出る / DB の確定が失敗すればイベントは出ない / 確定の後に送出が失われても再起動後に再送される。
- 変異: `UseDurableOutboxOnAllSendingEndpoints` を外す → 3 件目が赤（Outbox の行が 0）。失敗時の巻き戻しを外す → 2 件目が赤
  （確定しなかった削除のイベントが 1 通届く）。
- `WolverineTransactionalOutboxTests`（共通ヘルパの構成）、`WolverineDocumentDeletedPublisherTests`（確定の後の送出の失敗は例外にしない）、DocumentService.Tests（器の発行口は確定に成功した後でだけ記録する）。
