using System.Diagnostics.Metrics;
using WikiService.Common.Observability;

namespace WikiService.Tests;

// FR-13, [[IADR-0541]] 決定 5 (#1934): 計器（`WikiSyncSkippedMetrics`）は `IMeterFactory` を要求する
// （Web ホストでは `AddMetrics` で入る）。単体試験では器を自前で用意する（IngestionService.Tests の `TestMeterFactory` と同型）。
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
    internal static WikiSyncSkippedMetrics NewSkippedMetrics() => new(new TestMeterFactory());
}
