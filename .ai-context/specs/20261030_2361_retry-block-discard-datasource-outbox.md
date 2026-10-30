---
title: 作業仕様書 — 再試行の上限で捨てられる送出を DataSourceService で例外にし、捨てたことを計器と警報にする（#2361）
type: spec
status: done
related_ids: [FR-01, FR-12, UC-04, NFR-21, ADR-0027, IADR-0609, IADR-0656, IADR-0657]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2361"
---

# 作業仕様書 — 再試行の上限で捨てられる送出の欠落（#2361）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `a0d166b1`）。コーディネータの承認（2026-10-30）: 案 A ＋ B ＋ C。

## 背景（#2360 のレビューで測った事実）

1. `JasperFx.Blocks.RetryBlock<T>`（JasperFx 2.37.0）の既定は `MaximumAttempts = 3`・`Pauses = 0.05／0.1／0.25 秒`である。
   - 最初の 1 回 ＋ 再試行 3 回の後、`Discarding message {Message} after {Attempts} attempts`（Information・例外なし）を出して捨てる。
   - Wolverine 6.24.4 は上限を変えていない（`set_MaximumAttempts`・`set_Pauses` の参照 0 件）。ブロックは 27 か所の private フィールドにあり、差し替える口は無い。
2. 素の `IMessageBus.PublishAsync` を耐久の送出先へ出すと、`DurableSendingAgent` は送出表への書き込み（`StoreOutgoingAsync`）をブロックへ渡す。
   - 処理が成功すれば `PostAsync` は処理を待つ。**失敗すると最初の失敗の後に例外なしで返り**、再試行は裏で続く。
   - したがって **DataSourceService の同期**（`DataSourceSyncService` の `bus.PublishAsync`）は、表への書き込みが約 0.5 秒より長く失敗すると、**例外なしで送出を失い、watermark を進める**。
3. DocumentService・ConversionService は `IDbContextOutbox` でアプリのトランザクションの中に envelope を書く。表への書き込みが失敗すれば確定全体が例外で巻き戻る（**欠落しない**）。
   - 確定の後の送出・後片付けが捨てられると、重複は起き得るが欠落はしない。受信（Buffered）が捨てられても再配送される。

## 方針（IADR-0657。IADR-0609 決定 2 を置き換える）

- **A. DataSourceService の発行を 1 件ごとの短い Transactional Outbox にする。**
  - 新しい口 `IRawDocumentFetchedPublisher`（Domain/Ports）。実装 `WolverineRawDocumentFetchedPublisher` は `IDbContextOutbox<DataSourceDbContext>` で「開始 → 発行（envelope の INSERT）→ コミット → 送出」を 1 件ごとに行う。
  - 表への書き込みが失敗すれば例外になり、その 1 件は `failed` に数えられ、**watermark は進まない**（既存の `SyncResult.ShouldAdvanceWatermark`）。
  - 形は ConversionService の `WolverineConversionRetryPublisher` に揃える（`AllowMultiples`、失敗時の `ClearAllAsync` と巻き戻し、確定の後の送出の失敗は Warning）。
  - watermark の保存とは同じトランザクションに載せない（決定 2 の「載せない」は維持。変えるのは「表への書き込みの成否を呼び出し側が知る」こと）。
- **B. 捨てたこと自体を計器と警報にする。**
  - 計器 `messaging.retry_block.discarded`（タグなし）。照合は `{OriginalFormat}` = `Discarding message {Message} after {Attempts} attempts` の完全一致 ＋ Information 以上。
  - 警報 `MessagingRetryBlockDiscarded`（warning。`sum by (job) ((m unless m offset 1h) or increase(m[1h])) > 0`、`for: 5m`、`absent()` なし）を 4 か所の写しに置き、promtool の試験を足す。
- **C. 上限の延長（反射で private のブロックの `MaximumAttempts` を書き換える）は採らない。** IADR に却下理由を書く。

## 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| 1 | DataSourceService で送出表への書き込みが失敗すると、その 1 件は失敗に数えられ watermark は進まない | 統合 `RawDocumentFetchedStoreFailureTests`（A の前は赤） |
| 2 | 失敗が無ければ、1 件ごとの確定で発行され、Outbox の表は空に戻り watermark は進む | 同上の対照 |
| 3 | DocumentService・ConversionService は表への書き込みが失敗すると確定全体が例外で巻き戻る（固定） | 統合 `DocumentDeletedOutboxTests`・`ConversionRetryOutboxTests` に 1 件ずつ |
| 4 | 実物の `RetryBlock` が上限で捨てると、計器 `messaging.retry_block.discarded` が 1 進む。Debug や似たテンプレートは数えない | 単体 `RetryBlockErrorMetricsTests` |
| 5 | 警報 `MessagingRetryBlockDiscarded` が 4 か所で同じ式を持ち、promtool で初回を鳴らす | repo テスト ＋ `messaging-retry-block.test.yml`（CI） |
| 6 | 既存の単体試験（DataSourceService の同期）が新しい口で緑 | `DataSourceService.Tests` |

### 失敗の注入（コーディネータ条件 2）

- 試験ごとに**専用の DB**（`CREATE DATABASE`）を作り、その DB の `wolverine.wolverine_outgoing_envelopes` に `BEFORE INSERT` で例外を投げるトリガを置く。
- 片付け（トリガと関数の `DROP`）は `finally` で必ず行う。共有の DB・ロールの権限には触れない（他の並列の試験から隔離される）。
- 赤の確かめ方（条件 1）: 赤の試験だけのコミットを push し、Integration を workflow_dispatch で回して赤を確かめる。PR は修正を積んで緑にしてから作る。
  - 結果: 赤（068404145）https://github.com/endazon/microservices-platform/actions/runs/41018967808 —— 本題の 1 件だけが赤（`Fetched` が 1）。緑（0986ecfe1）https://github.com/endazon/microservices-platform/actions/runs/41021442049 —— 4 件とも緑。

## 性能の見積もり（条件 4。詳細は IADR-0657）

- 従前も耐久の送出先は 1 件ごとに表へ INSERT（自動コミット）していた。増えるのは `BEGIN`・`COMMIT` の往復 2 回／件である。
- 同じクラスタ内の PostgreSQL の往復を 0.5〜2 ms とすると、1 件あたり 1〜4 ms 増える。1 万件の初回の全走査で 10〜40 秒増える。
- 1 件の取得（コネクタの読み出し ＋ オブジェクトストレージへの格納）は数十〜数百 ms なので、同期の総時間の増加は数 % 以内と見積もる。

## 母集合（規則 9・10）

誤りの側の文字列 `欠落の窓`・`IADR-0609 決定 2`・`watermark だけが進み`・`欠落し得る` で develop を走査した（`git grep`。`.ai-context/specs`・`superpowers` を除く）。

| 対象 | 扱い |
| --- | --- |
| `docs/operations/operations.md` §送出の再試行中の欠落の窓・障害対応表の行 | 直す（DataSourceService は欠落しなくなる。Document／Conversion は欠落せず重複のみ。捨てた計器と警報を足す） |
| 警報 `MessagingRetryBlockErrors` の説明文（4 か所）と promtool の期待値 | 直す（「DataSourceService では watermark だけが進み」を外す） |
| `IADR-0609` 残余リスク | 本文は書き換えず、後継（IADR-0657）を日付つき追記で併記する |
| `IADR-0656` 帰結・残余リスク（捨てたことは計器にしていない） | 日付つき追記で IADR-0657 を併記する |
| `DataSourceSyncService.cs`・`Program.cs`・`RawDocumentFetchedDurableSendTests.cs` のコメント | 直す |
| `RetryBlockErrorMetrics.cs`・`ObservabilityExtensions.cs`・試験・repo テストのコメント（IADR-0609 決定 2 の引用） | 計器の起点としての引用で、誤りではない。`RetryBlockErrorMetrics.cs` は捨てた計器を足すので IADR-0657 を併記する |
| issue #2361 の「影響」 | 事実 2・3 に合わせて訂正するコメントを付ける（条件 3） |

除外: `.ai-context/specs/20261030_2178_*`（凍結の記録）。

## 残余リスク

- 捨てた計器はロガーの最小の段が Information を落とす構成では進まない（既定は Information）。
- Document／Conversion の確定の後の送出・後片付けが捨てられた場合の重複は残る（下流は冪等）。
- 1 件ごとの確定の間にプロセスが止まると、確定済みの envelope は表に残り、耐久エージェントが再送する（重複は起き得る）。

## PoC 配備への影響（条件 5）

- DataSourceService は既に `UsePlatformTransactionalOutbox` を呼んでおり、`wolverine` スキーマの表は起動時に作られている（#2169）。新しい表・EF の Migration は要らない。
- 警報が 1 本増える（Grafana の暫定経路の数 29 → 30）。
