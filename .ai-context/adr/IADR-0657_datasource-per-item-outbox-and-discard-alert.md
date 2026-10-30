---
title: IADR-0657 データソース同期の RawDocumentFetched は 1 件ごとの短い Transactional Outbox で発行し、送出表へ書けなければその 1 件を失敗にする。再試行の上限で捨てたことは計器と警報にする（IADR-0609 決定 2 を置き換え）
type: impl-adr
status: Accepted
related_ids: [FR-01, FR-12, UC-04, NFR-21, ADR-0027, ADR-0006, IADR-0609, IADR-0656, IADR-0604, IADR-0593]
author: claude
created: 2026-10-30
updated: 2026-10-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md（§決定「イベント発行の原子性は Wolverine の EF Core + PostgreSQL Transactional Outbox を標準とする」）
related_specs:
  - ../specs/20261030_2361_retry-block-discard-datasource-outbox.md
---

# IADR-0657: 再試行の上限で捨てられる送出の欠落を、データソース同期で例外にする（#2361）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-30
- 決定者: claude（#2361）。案 A ＋ B ＋ C はコーディネータの承認（2026-10-30）による。
- 置き換える決定: [IADR-0609](./IADR-0609_transactional-outbox-other-services.md) 決定 2（データソース同期は確定に載せず、耐久送出だけにする）。

## 起点・関連

- 起点 issue: #2361（#2360 のレビューで、JasperFx の `RetryBlock` の上限を実測して分かった欠落の経路）。
- 前提: [IADR-0656](./IADR-0656_retry-block-error-metric-instead-of-loki-rule.md)（再試行の失敗の計器と警報。§再試行の上限が実測）。
- 計画: ADR-0027 §決定（発行の原子性は Transactional Outbox を標準とする）、ADR-0006（観測の統一計装）。

## コンテキストと課題

IADR-0609 決定 2 は、DataSourceService の同期の発行を素の `IMessageBus.PublishAsync` のまま耐久の送出先へ出した。
「送出の前に Outbox の表へ書くので、watermark を進める保存より前に発行は表に残る」と考えたためである。#2361 の実測で、これが成り立たない場合があると分かった。

1. `DurableSendingAgent` は表への書き込み（`StoreOutgoingAsync`）を `JasperFx.Blocks.RetryBlock<T>` に渡す。
   - 処理が成功すれば `PostAsync` は処理を待つ。**失敗すると最初の失敗の後に例外なしで返り**、再試行は裏で続く。
   - ブロックの既定は `MaximumAttempts = 3`・`Pauses = 0.05／0.1／0.25 秒`である。最初の 1 回 ＋ 再試行 3 回の後、`Discarding message {Message} after {Attempts} attempts`（Information・例外なし）を出して捨てる。
   - Wolverine 6.24.4 は上限を変えない（`set_MaximumAttempts`・`set_Pauses` の参照 0 件）。ブロックは 27 か所の private フィールドで、差し替える口は無い。
2. したがって、表への書き込みが約 0.5 秒より長く失敗すると、同期は発行を成功と数え、**watermark を進め、原本は恒久に欠落する**。プロセスが動き続けていても起きる。
3. DocumentService・ConversionService は `IDbContextOutbox` で、アプリのトランザクションの中で envelope を書く。表への書き込みの失敗は確定全体の例外になり、巻き戻る（欠落しない）。
   - 確定の後の送出・後片付けが捨てられると、重複や遅れは起き得るが欠落はしない。受信（Buffered）が捨てられても、ブローカが再配送する。

## 決定

### 決定 1（A）: データソース同期の発行は 1 件ごとの短い Transactional Outbox にする

- 発行口 `IRawDocumentFetchedPublisher`（Domain/Ports）を置く。約束は「**返ったときには送出表にコミット済み。書けなければ例外**」である。
- 実装 `WolverineRawDocumentFetchedPublisher` は `IDbContextOutbox<DataSourceDbContext>` で、1 件ごとに「開始 → 発行（envelope の INSERT）→ コミット → 送出」を行う。
  - envelope の INSERT はこのトランザクションの中で直接走るので、表へ書けなければ例外になる（`RetryBlock` を通らない）。
  - 形と失敗の扱いは ConversionService の `WolverineConversionRetryPublisher` と同じである（`AllowMultiples`、失敗時の `ClearAllAsync` と巻き戻し、外側のトランザクションの拒否、確定の後の送出の失敗は Warning）。
- `DataSourceSyncService` はこの口だけを使う。例外はその 1 件の失敗に数えられ（既存の catch）、`SyncResult.ShouldAdvanceWatermark` が偽になる。watermark は進まず、次の同期で取り直す。
- watermark の保存とは同じトランザクションに載せない（IADR-0609 決定 2 の「載せない」は維持する）。
  - IADR-0609 が退けたのは「同期全体を 1 つのトランザクションに載せる」案である（コネクタの取得と格納の間、トランザクションが開いたままになる）。本決定のトランザクションは 1 件の envelope の INSERT だけを包み、取得と格納の間は開いていない。

### 決定 2（B）: 再試行の上限で捨てたことを計器と警報にする

- 計器 `messaging.retry_block.discarded`（Prometheus では `messaging_retry_block_discarded_total`）。`RetryBlockErrorMetrics` に足し、同じ提供者が数える。
  - 照合は `{OriginalFormat}` = `Discarding message {Message} after {Attempts} attempts` の完全一致 ＋ Information 以上である。例外は付かないので見ない。
  - ラベルは持たない（`{Message}` は envelope で、中身を載せない）。サービスは `job` で分かる。
  - 試験は実物の `RetryBlock<T>` に常に失敗する処理を渡し、上限の回数だけ試して捨て、計器が 1 進むことを測る。JasperFx の版上げでテンプレートや上限が変われば赤になる。
- 警報 `MessagingRetryBlockDiscarded`（warning）は `MessagingRetryBlockErrors` と同じ形にする（`sum by (job) ((m unless m offset 1h) or increase(m[1h])) > 0`、`for: 5m`、`absent()` なし、初回の腕は系列ごと〔IADR-0593〕）。
  - 置き場所は 4 か所の写し（経路 A / B・暫定の Grafana の 2 か所）。場面は `deploy/prometheus/tests/messaging-retry-block.test.yml` が promtool で固定する。

### 決定 3（C）: 再試行の上限を延ばす改造はしない

- 反射で private のブロックの `MaximumAttempts`・`Pauses` を書き換える案は採らない（却下した案を参照）。

## 却下した案

- **C. 反射で上限を延ばす**: 採らない。
  - ブロックは Wolverine の内部の 27 か所の private フィールドにあり、名前も型も公開の約束ではない。版上げで黙って効かなくなる。
  - 延ばしても上限はなくならない。失敗が続けば、いつかは例外なしで捨てる。「呼び出し側が失敗を知る」ことにはならない。
  - 延ばすほど、裏の再試行の間に呼び出し側の確定が進む窓が長くなる。
- **watermark の保存と同じトランザクションに載せる**: 採らない。IADR-0609 決定 2 の理由（同期全体にわたってトランザクションが開く）がそのまま当てはまる。
- **確定を止める（送出の失敗で同期全体を止める）**: 採らない。1 件の失敗をその 1 件の失敗に数える既存の仕組み（連続失敗・watermark を進めない）で足りる。
- **issue の「設定で上限を延ばす」**: 設定の口が無い（上の事実 1）。延ばすには決定 3 の改造が要る。

## 性能の見積もり（コーディネータ条件 4）

- 従前も、耐久の送出先は 1 件ごとに送出表へ INSERT していた（自動コミット）。本決定で増えるのは、1 件ごとの `BEGIN`・`COMMIT` の往復 2 回である。
- 同じクラスタ内の PostgreSQL の往復を 0.5〜2 ms とすると、1 件あたり 1〜4 ms 増える。
  - 1 万件の初回の全走査で 10〜40 秒増える。
  - 増分の同期（数件〜数百件）では 1 秒未満である。
- 1 件の取得はコネクタの読み出し（ファイル・HTTP・DB）とオブジェクトストレージへの格納で、数十〜数百 ms かかる。同期の総時間の増加は数 % 以内と見積もる。
- 送出（`FlushOutgoingMessagesAsync`）は従前と同じく送出エージェントへ渡すだけで、ブローカの応答は待たない。

## 結果

- 良い影響:
  - DataSourceService の同期は、送出表へ書けなかった原本を失敗に数え、次の同期で取り直す。#2361 の欠落の経路は閉じた。
  - 3 サービスの Wolverine の発行は、どれも「送出表へのコミットを確かめてから成功を返す」形になった。表への書き込みの一時失敗で欠落する経路は、知られている範囲で無い。
  - 捨てたこと自体が計器と警報で見える。
- 悪い影響 / トレードオフ:
  - 1 件ごとに `BEGIN`・`COMMIT` の往復が 2 回増える（上の見積もり）。
  - 表への書き込みの失敗は、その 1 件の失敗として連続失敗に数えられる。DB が長く不調なら、SC-06 の継続失敗の警告に現れる（意図どおり）。
- 残余リスク:
  - 確定の後の送出・後片付けが上限で捨てられると、重複や遅れは起き得る。envelope は送出表に残り、耐久エージェント（または再起動）が送る。下流は冪等である。
  - 新しい発行を素の `IMessageBus.PublishAsync` で足すと、同じ欠落の経路が戻る。機械の検査は置いていない（同型の事故が 2 回起きたら検査器を考える）。発行口のコメントと本 IADR で止める。
  - 捨てた計器は、ロガーの最小の段が Information を落とす構成では進まない（既定は Information）。
- PoC 配備への影響: DataSourceService は既に `UsePlatformTransactionalOutbox` を呼んでおり、`wolverine` スキーマの表は起動時に作られている。新しい表・EF の Migration は要らない。警報が 1 本増える（Grafana の暫定経路で 29 → 30 件）。

## 検証

- 統合 `RawDocumentFetchedStoreFailureTests`（Knowledge.IntegrationTests。DataSourceService の本番の配線 ＋ 実 PostgreSQL ＋ 実 RabbitMQ）2 件:
  - 送出表のトリガで書き込みを拒否すると、`Failed = 1`・watermark は進まない。**本決定の前のコードでは赤**（赤のコミットを Integration の workflow_dispatch で確かめた）。
  - 拒否しなければ発行され、送出表は空に戻り、watermark が進む。
  - 失敗の注入は試験ごとの専用の DB にだけ置き、片付け（トリガの DROP）は `finally` で行う。
- 統合 `DocumentDeletedOutboxTests`・`ConversionRetryOutboxTests` に 1 件ずつ: 同じ注入で確定全体が例外で巻き戻る（固定）。
- 単体 `RetryBlockErrorMetricsTests`: 実物の `RetryBlock` が上限で捨てると、捨てた計器が 1 進む。Debug や似たテンプレートは数えない。
- repo テスト（`scripts.repo.test.js`）: 4 か所の式を C# の計器名から導いて照合する。promtool の試験（CI）が初回・再起動後の 1 件目を固定する。
