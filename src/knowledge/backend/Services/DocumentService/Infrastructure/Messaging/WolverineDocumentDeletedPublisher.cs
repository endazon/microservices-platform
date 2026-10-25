using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.Logging;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime;

namespace DocumentService.Infrastructure.Messaging;

// FR-06 / ADR-0027（E3a）: DocumentDeleted の発行（Wolverine）。
//
// 🔴 **本ファイルには `using MassTransit;` を入れてはならない。**
// `check-event-topology.js` の `transportsOfFile` はファイル中の `using` からトランスポートを
// 導出する。両方が同居すると、この発行が `masstransit+wolverine` の両方として記録され、
// `transportMismatches()` の発行側 union に masstransit が混ざる（逆行の隠蔽）。
// 1 ファイル 1 トランスポートを保つ（E1 の MassTransitDocumentNormalizedPublisher と同じ作法）。
// （`using Wolverine.EntityFrameworkCore;` は同検査の `using Wolverine` に一致し、Wolverine として数えられる。）
//
// 配置は IADR-0280 の写像では Adapters → Infrastructure だが、トランスポート実装は
// 合成ルート（Api）が持つ Platform.Shared.Infrastructure / WolverineFx.RuntimeCompilation に
// 依存するため、messaging パッケージを Infrastructure 骨格へ持ち込まず Api 側へ置く
// （判断の記録は作業仕様書 20260828_edge-e3a-document-deleted.md §配置）。
//
// FR-06, FR-19, ADR-0027 §決定, ADR-0057 決定 1・3, IADR-0604 (#2042): **Transactional Outbox 経由で発行する。**
// `IDbContextOutbox<DocumentDbContext>` はスコープ内の `DocumentDbContext` と同じインスタンスを持つ
// （DI のスコープで解決されるため）。発行は envelope として DbContext のトランザクションへ INSERT され、
// `SaveChangesAndFlushAsync` が DB の変更と一緒にコミットしてから送る。
public sealed class WolverineDocumentDeletedPublisher(
    IDbContextOutbox<DocumentDbContext> outbox, ILogger<WolverineDocumentDeletedPublisher> logger)
    : IDocumentDeletedPublisher
{
    public async Task PublishDeletedAsync(
        Guid documentId, DateTimeOffset deletedAt, long? sequence = null, CancellationToken ct = default)
    {
        // 🔴 イベントの構築をここに置くのは、`findPublishers` が `Publish(new <Event>(` にしか
        // 一致しないためである（組み立て済みの変数を渡すと発行が検査器から見えなくなる）。
        // ⚠️ Wolverine の PublishAsync は CancellationToken を取らない（E1 仕様書「受け入れた挙動差」で受容済みの API 差）。
        // IADR-0604: ここで DB のトランザクションが開き（開いていなければ）、envelope が INSERT される。送出はまだしない。
        await outbox.PublishAsync(new DocumentDeleted(documentId, deletedAt, sequence));
    }

    public async Task SaveChangesAndFlushAsync(CancellationToken ct = default)
    {
        // 🔴 `AllowMultiples`: 既定（OnlyOnce）は 2 回目以降の送出を**警告 1 行で黙って捨てる**（envelope は表に残るが、
        // 送出側の所有のまま再送の対象にならない）。定期処理は 1 スコープで所有者ごと・段ごとに確定する。
        if (outbox is MessageContext messaging)
            messaging.MultiFlushMode = MultiFlushMode.AllowMultiples;

        // ① 確定（DB の変更と積んだ envelope を 1 トランザクションでコミットする）。
        var db = outbox.DbContext;
        try
        {
            await db.SaveChangesAsync(ct);
            if (db.Database.CurrentTransaction is not null)
                await db.Database.CommitTransactionAsync(ct);
        }
        catch
        {
            // 🔴 確定が失敗したら、積んだ発行を捨ててトランザクションを巻き戻す。巻き戻さないと、開いたままの
            // トランザクションに envelope の INSERT が残り、同じスコープの後続の確定（定期処理の次の段）と一緒に
            // コミットされて**消えていない文書の削除イベント**が出る（実測。EF は `SaveChanges` の前にセーブポイントを置き、
            // 失敗してもそこまでしか戻さないので、セーブポイントより前の envelope の INSERT は生き残る）。
            if (outbox is MessageContext context)
                await context.ClearAllAsync();
            if (db.Database.CurrentTransaction is { } open)
                await open.RollbackAsync(CancellationToken.None);
            throw;
        }

        // ② 送出。🔴 **確定の後の送出の失敗は呼び出し側へ出さない**（IADR-0604 決定 2）。削除は確定済みであり、
        // 送れなかったイベントは Outbox の表に残って耐久エージェントが再送する。例外を出すと、確定済みの削除に 500 を返し、
        // 利用者がやり直すと 404 になる（結果が紛らわしい）。失敗は Warning で残す。
        try
        {
            await outbox.FlushOutgoingMessagesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "削除イベントの送出に失敗した。削除は確定済みで、イベントは Outbox の表に残り再送される");
        }
    }
}
