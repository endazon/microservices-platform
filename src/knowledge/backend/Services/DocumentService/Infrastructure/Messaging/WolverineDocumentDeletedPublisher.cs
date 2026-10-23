using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Events;
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
public sealed class WolverineDocumentDeletedPublisher(IDbContextOutbox<DocumentDbContext> outbox)
    : IDocumentDeletedPublisher
{
    public async Task PublishDeletedAsync(
        Guid documentId, DateTimeOffset deletedAt, CancellationToken ct = default) =>
        // 🔴 イベントの構築をここに置くのは、`findPublishers` が `Publish(new <Event>(` にしか
        // 一致しないためである（組み立て済みの変数を渡すと発行が検査器から見えなくなる）。
        // ⚠️ Wolverine の PublishAsync は CancellationToken を取らない（発行のキャンセル伝播は
        // 失われる。E1 仕様書「受け入れた挙動差」で受容済みの API 差）。
        // ⚠️ ここで初めて DB のトランザクションが開く（envelope の INSERT のため。開いていなければ開く）。
        await outbox.PublishAsync(new DocumentDeleted(documentId, deletedAt));

    public async Task SaveChangesAndFlushAsync(CancellationToken ct = default)
    {
        try
        {
            // 🔴 `AllowMultiples`: 既定（OnlyOnce）は 2 回目以降の送出を**警告 1 行で黙って捨てる**（envelope は表に残るが、
            // 送出側の所有のまま再送の対象にならない）。定期処理は 1 スコープで所有者ごと・段ごとに確定する。
            await outbox.SaveChangesAndFlushMessagesAsync(MultiFlushMode.AllowMultiples, ct);
        }
        catch
        {
            // 🔴 確定が失敗したら、積んだ発行を捨ててトランザクションを巻き戻す。巻き戻さないと、開いたままの
            // トランザクションに envelope の INSERT が残り、同じスコープの後続の確定（定期処理の次の段）と一緒に
            // コミットされて**消えていない文書の削除イベント**が出る（実測。EF は `SaveChanges` の前にセーブポイントを置き、
            // 失敗してもそこまでしか戻さないので、セーブポイントより前の envelope の INSERT は生き残る）。
            if (outbox is MessageContext context)
                await context.ClearAllAsync();
            if (outbox.DbContext.Database.CurrentTransaction is { } open)
                await open.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
