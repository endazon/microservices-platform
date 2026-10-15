using System.Diagnostics.Metrics;
using IngestionService.Common.Observability;

namespace IngestionService.Tests;

// FR-02, [[IADR-0536]] 決定 3 (#1908): 計器（`EmbeddingPendingMetrics`）は `IMeterFactory` を要求する
// （Web ホストでは既定で入る）。単体試験では器を自前で用意する（RetrievalService.Tests の `TestMeterFactory` と同型）。
// **Meter 名を毎回ユニークにする**のは、同名 Meter の再利用で計測が他の試験と混ざるのを防ぐためである。
internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = [];

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter($"{options.Name}.test-{Guid.NewGuid():N}", options.Version,
            options.Tags, scope: this);
        _meters.Add(meter);
        return meter;
    }

    public void Dispose()
    {
        foreach (var m in _meters) m.Dispose();
        _meters.Clear();
    }

    // 実測に使わない場面（コンストラクタを満たすだけ）のための近道。
    internal static EmbeddingPendingMetrics NewEmbeddingPendingMetrics() => new(new TestMeterFactory());

    // #1931 / [[IADR-0540]]: 本文の欠落の計数（同上）。
    internal static BodyMissingMetrics NewBodyMissingMetrics() => new(new TestMeterFactory());
}
