using DataSourceService.Domain.Ports;
using DataSourceService.Infrastructure.Persistence;
using Knowledge.Contracts.Events;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime;

namespace DataSourceService.Infrastructure.Messaging;

// FR-01, UC-04, ADR-0027 §決定, IADR-0657 (#2361): `RawDocumentFetched` を **1 件ごとの短い Transactional Outbox** で発行する。
//
// 「開始 → 発行（envelope を送出表へ INSERT）→ コミット → 送出」を 1 件ごとに行う。envelope の INSERT はこのトランザクションの
// 中で直接走るので、表へ書けなければ**例外になる**（JasperFx の `RetryBlock` を通らない）。
// watermark の保存とは同じトランザクションに載せない（保存は呼び出し側が同期の最後に行う。IADR-0609 決定 2 の「載せない」は維持）。
//
// 形と失敗の扱いは ConversionService の `WolverineConversionRetryPublisher`（IADR-0609）と同じである。
// 🔴 **本ファイルには `using MassTransit;` を入れてはならない**（トポロジ検査の transportsOfFile。1 ファイル 1 トランスポート）。
public sealed class WolverineRawDocumentFetchedPublisher(
    IDbContextOutbox<DataSourceDbContext> outbox, ILogger<WolverineRawDocumentFetchedPublisher> logger)
    : IRawDocumentFetchedPublisher
{
    public async Task PublishAsync(RawDocumentFetched ev, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ev);

        // 🔴 外側のトランザクションの中では呼べない。ここでのコミットが外側を勝手に確定し、巻き戻しは外側の変更まで捨てるためである。
        var db = outbox.DbContext;
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException(
                "IRawDocumentFetchedPublisher.PublishAsync は DbContext のトランザクションが開いていない状態で呼ぶ（IADR-0657）。"
                + " 開いた外側のトランザクションを、この発行が勝手にコミットしないためである。");

        // 🔴 `AllowMultiples`: 同期は 1 つのスコープで何件も発行する。既定（OnlyOnce）は 2 回目以降の送出を
        // **警告 1 行で黙って捨てる**（IADR-0604 決定 2）。
        if (outbox is MessageContext messaging)
            messaging.MultiFlushMode = MultiFlushMode.AllowMultiples;

        // ① 確定。⚠️ DbContext に実行戦略（`EnableRetryOnFailure` 等）を有効化する場合は、全体を
        // `CreateExecutionStrategy().ExecuteAsync` で包む必要がある（IADR-0609 と同じ。いまは無効）。
        try
        {
            await db.Database.BeginTransactionAsync(ct);
            await outbox.PublishAsync<RawDocumentFetched>(ev);
            await db.Database.CommitTransactionAsync(ct);
        }
        catch
        {
            // 🔴 積んだ発行を捨ててトランザクションを巻き戻す（同じスコープの次の 1 件の確定に、確定しなかった発行を混ぜない）。
            if (outbox is MessageContext context)
                await context.ClearAllAsync();
            if (db.Database.CurrentTransaction is { } open)
                await open.RollbackAsync(CancellationToken.None);
            throw;
        }

        // ② 送出。🔴 **確定の後の送出の失敗は呼び出し側へ出さない。** イベントは送出表にコミット済みで、
        // 耐久エージェントが再送する。例外にすると、確定済みの 1 件を失敗に数えて次の同期で再発行する（重複が増えるだけ）。
        try
        {
            await outbox.FlushOutgoingMessagesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "取得した原本のイベントの送出に失敗した。イベントは送出表にコミット済みで、再送される（source {SourceId}）",
                ev.SourceId);
        }
    }
}
