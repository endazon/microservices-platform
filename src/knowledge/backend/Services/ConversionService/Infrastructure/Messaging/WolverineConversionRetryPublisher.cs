using ConversionService.Domain.Ports;
using ConversionService.Infrastructure.Persistence;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.Logging;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime;

namespace ConversionService.Infrastructure.Messaging;

// FR-12 例外フロー, UC-06, ADR-0027 §決定, IADR-0609 (#2169): 再変換の `RawDocumentFetched` を Transactional Outbox 経由で発行する。
//
// 🔴 **本ファイルには `using MassTransit;` を入れてはならない**（トポロジ検査の transportsOfFile の union 汚染。
// 1 ファイル 1 トランスポート。`MassTransitDocumentNormalizedPublisher` と分けてあるのと同じ理由）。
//
// `IDbContextOutbox<ConversionJobDbContext>` はスコープ内の `ConversionJobDbContext` と同じインスタンスを持つ
// （DI のスコープで解決される）。形と失敗の扱いは DocumentService の `WolverineDocumentUpdatedPublisher`（IADR-0608）と同じである。
public sealed class WolverineConversionRetryPublisher(
    IDbContextOutbox<ConversionJobDbContext> outbox, ILogger<WolverineConversionRetryPublisher> logger)
    : IConversionRetryPublisher
{
    // `CommitAsync` の関数の中に居るか。外で積まれた発行は確定されないまま捨てられるので、拒否する（IADR-0608 決定 2 と同じ）。
    private bool _inCommit;

    public async Task PublishRetryAsync(RawDocumentFetched ev, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ev);
        if (!_inCommit)
            throw new InvalidOperationException(
                "再変換の RawDocumentFetched は IConversionRetryPublisher.CommitAsync の関数の中でだけ積める（IADR-0609）。"
                + " 外で積むと DB の確定と同じトランザクションに載らず、確定されないまま捨てられる。");

        // envelope は `CommitAsync` が開いたトランザクションへ INSERT される。送出はまだしない。
        await outbox.PublishAsync(ev);
    }

    public async Task CommitAsync(Func<CancellationToken, Task> saveAndPublish, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(saveAndPublish);
        if (_inCommit)
            throw new InvalidOperationException("IConversionRetryPublisher.CommitAsync は入れ子にできない（IADR-0609）。");

        // 🔴 外側のトランザクションの中では呼べない（IADR-0608 決定 2 と同じ）。ここでのコミットが外側を勝手に確定し、
        // 巻き戻しは外側の変更まで捨てるためである。
        var db = outbox.DbContext;
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException(
                "IConversionRetryPublisher.CommitAsync は DbContext のトランザクションが開いていない状態で呼ぶ（IADR-0609）。"
                + " 開いた外側のトランザクションを、この確定が勝手にコミットしないためである。");

        // 🔴 `AllowMultiples`: 既定（OnlyOnce）は 2 回目以降の送出を**警告 1 行で黙って捨てる**（IADR-0604 決定 2）。
        if (outbox is MessageContext messaging)
            messaging.MultiFlushMode = MultiFlushMode.AllowMultiples;

        // ① 確定。トランザクションを**保存より前に**開く —— ストアの保存（`PrepareRetryAsync` の `SaveChangesAsync`）が
        // その中で流れ、envelope の INSERT も同じトランザクションに載る。
        try
        {
            await db.Database.BeginTransactionAsync(ct);
            _inCommit = true;
            try
            {
                await saveAndPublish(ct);
            }
            finally
            {
                _inCommit = false;
            }
            await db.Database.CommitTransactionAsync(ct);
        }
        catch
        {
            // 🔴 確定が失敗したら、積んだ発行を捨ててトランザクションを巻き戻す（IADR-0604 決定 2 と同じ理由。巻き戻さないと、
            // 確定しなかった再変換のイベントが同じスコープの後続の確定と一緒に送られる）。
            if (outbox is MessageContext context)
                await context.ClearAllAsync();
            if (db.Database.CurrentTransaction is { } open)
                await open.RollbackAsync(CancellationToken.None);
            throw;
        }

        // ② 送出。🔴 **確定の後の送出の失敗は呼び出し側へ出さない**（IADR-0604 決定 2）。ジョブは queued で確定済みであり、
        // 送れなかったイベントは Outbox の表に残って耐久エージェントが再送する。例外にすると確定済みの再変換に 500 を返し、
        // やり直すと 409（失敗以外は再変換不可）になる。失敗は Warning で残す。
        try
        {
            await outbox.FlushOutgoingMessagesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "再変換のイベントの送出に失敗した。ジョブは queued で確定済みで、イベントは Outbox の表に残り再送される");
        }
    }
}
