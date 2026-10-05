using System.Diagnostics.Metrics;
using AwesomeAssertions;
using GraphService.Common.Observability;
using Microsoft.Extensions.DependencyInjection;

namespace GraphService.Tests.Common.Observability;

// FR-17, SC-10, ADR-0076 決定 3, [[IADR-0496]] 決定 5 (#1733, T-76): 日次のクラスタ検出が**最後に成功した時刻**の系列。
//
// 検出が止まってもクラスタの行は最後の値のまま残り、画面は正常に見える（#1733 は 1 週間気付かれなかった）。
// 遅れは `time() - graph_cluster_detection_last_success_timestamp_seconds` で読む。
[Trait("TestKind", "Unit")]
public sealed class ClusterDetectionMetricsTests
{
    [Fact]
    public void 計器の名前と単位とMeterは決めたとおりである()
    {
        ClusterDetectionMetrics.LastSuccessGaugeName.Should().Be("graph.cluster_detection.last_success.timestamp",
            "Prometheus 側では graph_cluster_detection_last_success_timestamp_seconds になる");
        ClusterDetectionMetrics.MeterName.Should().Be(EdgeTypeFallbackMetrics.MeterName,
            "同じ Meter に載せる（Program.cs の AddMeter を増やさない）");
    }

    // 🔴 記録を読むまでは系列を出さない。0 を出すと「1970 年に成功した」になる。
    [Fact]
    public void 記録を読むまでは観測値を出さない()
    {
        var (_, probe) = NewProbe();

        probe.Observe().Should().BeEmpty();
    }

    [Fact]
    public void 読んだ成功の時刻をUnix秒で出す()
    {
        var (metrics, probe) = NewProbe();
        var at = new DateTimeOffset(2026, 9, 27, 3, 12, 19, TimeSpan.Zero);

        metrics.RecordLastSuccess(at);

        probe.Observe().Should().Equal(at.ToUnixTimeSeconds());
        probe.Unit.Should().Be("s");
    }

    // 記録が消えた（null を読んだ）ら、古い値を出し続けない。
    [Fact]
    public void 記録が無いと読んだら観測値を引っ込める()
    {
        var (metrics, probe) = NewProbe();
        metrics.RecordLastSuccess(DateTimeOffset.UnixEpoch.AddDays(1));

        metrics.RecordLastSuccess(null);

        probe.Observe().Should().BeEmpty();
    }

    internal static (ClusterDetectionMetrics Metrics, GaugeProbe Probe) NewProbe()
    {
        var factory = new ServiceCollection().AddMetrics().BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();
        var metrics = new ClusterDetectionMetrics(factory);
        return (metrics, new GaugeProbe(factory.Create(ClusterDetectionMetrics.MeterName)));
    }

    // **Meter のインスタンスと計器名で絞る**（Meter 名で絞ると並行実行の別容器の計器を拾う。[[IADR-0394]]）。
    internal sealed class GaugeProbe : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<long> _values = [];

        public string? Unit { get; private set; }

        public GaugeProbe(Meter meter)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (ReferenceEquals(instrument.Meter, meter)
                        && instrument.Name == ClusterDetectionMetrics.LastSuccessGaugeName)
                    {
                        Unit = instrument.Unit;
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => _values.Add(value));
            _listener.Start();
        }

        public IReadOnlyList<long> Observe()
        {
            _values.Clear();
            _listener.RecordObservableInstruments();
            return [.. _values];
        }

        public void Dispose() => _listener.Dispose();
    }
}
