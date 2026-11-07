using System.Diagnostics.Metrics;
using AwesomeAssertions;
using LlmGateway.Common.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace LlmGateway.Tests.Common.Observability;

// NFR-18, IADR-0698: 起動の時刻のゲージ。プロセスごとに起動の時刻（UNIX 秒）を 1 つ出し、作り直すと値が変わる
// （クラウドの警報 LlmGatewayRestartLoop は、この値の変わった回数で短い間の再起動の繰り返しを数える）。
[Collection(SharedMeterCollection.Name)]
[Trait("TestKind", "Unit")]
public sealed class LlmGatewayStartMetricsTests
{
    private static List<double> Observe(IMeterFactory factory, LlmGatewayStartMetrics _)
    {
        var seen = new List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == LlmGatewayStartMetrics.MeterName
                && instrument.Name == LlmGatewayStartMetrics.StartTimeGaugeName
                && ReferenceEquals(instrument.Meter.Scope, factory))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, v, _, _) => seen.Add(v));
        listener.Start();
        listener.RecordObservableInstruments();
        return seen;
    }

    [Fact]
    public void 起動の時刻をUNIX秒で1つ出し_作り直すと値が変わる()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
        using var first = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var f1 = first.GetRequiredService<IMeterFactory>();
        var m1 = new LlmGatewayStartMetrics(f1, time);

        Observe(f1, m1).Should().Equal(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());

        time.Advance(TimeSpan.FromSeconds(30));
        using var second = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var f2 = second.GetRequiredService<IMeterFactory>();
        Observe(f2, new LlmGatewayStartMetrics(f2, time)).Should().Equal(m1.StartedAtUnixSeconds + 30);
    }
}
