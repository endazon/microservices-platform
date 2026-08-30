using System.Diagnostics.Metrics;
using RetrievalService.Common.Observability;

namespace RetrievalService.Tests;

// FR-03, #1116: メトリクス（`KeywordSearchMetrics`）は `IMeterFactory` を要求する
// （Web ホストでは既定で入る）。単体テストでは器を自前で用意する。
// GraphService.Tests の `DummyMeterFactory` と同型。**Meter 名を毎回ユニークにする**のは、
// 同名 Meter の再利用で計測が他のテストと混ざるのを防ぐためである。
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
    internal static KeywordSearchMetrics NewKeywordSearchMetrics() =>
        new(new TestMeterFactory());
}
