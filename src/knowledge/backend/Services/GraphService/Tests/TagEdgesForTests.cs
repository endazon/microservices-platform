using System.Diagnostics.Metrics;
using GraphService.Common.Observability;
using GraphService.Domain.Ports;
using GraphService.Features.GraphDocuments.Sync;
using GraphService.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GraphService.Tests;

// FR-17, [[IADR-0521]] (#1396): 購読の受け口を手で組み立てる試験のための部品。
// 共有タグの辺の上限は本番の既定（50）。上限を変えて測る試験は `Synchronizer(db, max)` を使う。
internal static class TagEdgesForTests
{
    // `locks` を渡さなければ排他は no-op（InMemory は advisory lock もトランザクションも持たない）。
    // 排他の要求の順序を測る試験は記録する実装を渡す（[[IADR-0521]] 決定 7）。
    internal static TagEdgeSynchronizer Synchronizer(
        GraphDbContext db, int? maxDocumentsPerTag = null, ITagEdgeLocks? locks = null)
        => new(db,
            Options.Create(new TagEdgeOptions
            {
                MaxDocumentsPerTag = maxDocumentsPerTag ?? TagEdgeOptions.DefaultMaxDocumentsPerTag,
            }),
            locks ?? NoOpTagEdgeLocks.Instance,
            NullLogger<TagEdgeSynchronizer>.Instance);

    internal static LinkEdgeSynchronizer Links(GraphDbContext db)
        => new(db, new EdgeTypeFallbackMetrics(new NullMeterFactory()), NullLogger<LinkEdgeSynchronizer>.Instance);

    private sealed class NullMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options);
        public void Dispose() { }
    }
}
