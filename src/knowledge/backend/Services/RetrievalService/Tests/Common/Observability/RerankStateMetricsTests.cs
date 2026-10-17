using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using RetrievalService.Common.Observability;

namespace RetrievalService.Tests.Common.Observability;

// NFR-01, NFR-29, ADR-0129 決定 2, [[IADR-0559]] (#1746 段 S5 の前提):
// **再順位付けの構成のゲージ `search.rerank.enabled` が、OTel の収集の出口まで届く。**
//
// 検索 p95 の警報（`deploy/prometheus/alerts.yml` の `SearchLatencyP95High` / `SearchRerankLatencyTargetUndefined`）は
// このゲージで閾値を選ぶ。ゲージが届かないとルールは常に「無効」と読み、有効にしても 1.5 秒で鳴り続ける。
//
// 🔴 **MeterListener ではなく、本番の MeterProvider に読み手を足して測る。** MeterListener は Meter の作成そのものを拾うので、
// `AddMeter`（OTLP の収集対象への登録）が無くても緑になる —— 本件の欠陥（RetrievalService に AddMeter が無く、
// 検索の計器が 1 件も Prometheus へ届いていなかった）を測れない。
//
// 🔴 **自分のホストの点だけを数える**（PR #1987 の AI レビュー 🟡）。OTel の MeterProvider は同じ名前の Meter を
// プロセス内のすべてから拾うので、並列で起きた他の試験のホスト（既定＝無効）も同じゲージを 0 で出し、読み手に混ざる。
// Meter の名前は変えられない（`AddMeter` の照合そのものが試験の対象）ので、このホストの `IMeterFactory` だけが
// Meter に一意の属性（`test.host`）を付け、読み手はその属性を持つ Meter の点だけを集める。
[Trait("TestKind", "Integration")]
public class RerankStateMetricsTests
{
    private const string HostTag = "test.host";

    // 名前・版・属性はそのままに、このホストを識別する Meter の属性を 1 つ足す。
    private sealed class HostTaggingMeterFactory(string hostId) : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            var tags = (options.Tags ?? []).Append(new KeyValuePair<string, object?>(HostTag, hostId));
            var meter = new Meter(options.Name, options.Version, tags, scope: this);
            lock (_meters) _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            lock (_meters)
            {
                foreach (var m in _meters) m.Dispose();
                _meters.Clear();
            }
        }
    }

    private sealed class CapturingExporter(string hostId) : BaseExporter<Metric>
    {
        // このホストの Meter が出したゲージの点（名前 → 値の並び）。他のホストの点は入れない。
        public Dictionary<string, List<long>> Gauges { get; } = new();

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                if (metric.MetricType != MetricType.LongGauge) continue;
                var ours = metric.MeterTags?.Any(t => t.Key == HostTag && Equals(t.Value, hostId)) ?? false;
                if (!ours) continue;
                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    lock (Gauges)
                    {
                        if (!Gauges.TryGetValue(metric.Name, out var values)) Gauges[metric.Name] = values = [];
                        values.Add(point.GetGaugeLastValueLong());
                    }
                }
            }
            return ExportResult.Success;
        }
    }

    private sealed class CapturingFactory(BaseExportingMetricReader reader, string hostId, string? enabled) : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // 段の有無は `Program.cs` が Build の前に読むので `UseSetting` で渡す（ClaudeRerankWiringTests と同じ作法）。
            if (enabled is not null)
                builder.UseSetting("Rerank:Enabled", enabled);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMeterFactory>();
                services.AddSingleton<IMeterFactory>(new HostTaggingMeterFactory(hostId));
                services.ConfigureOpenTelemetryMeterProvider(b => b.AddReader(reader));
            });
        }
    }

    private static IReadOnlyList<long> Exported(string? enabled)
    {
        var hostId = Guid.NewGuid().ToString("N");
        var exporter = new CapturingExporter(hostId);
        using var reader = new BaseExportingMetricReader(exporter);
        using var factory = new CapturingFactory(reader, hostId, enabled);
        _ = factory.Services.GetRequiredService<MeterProvider>(); // ホストを起こす（MeterProvider と起動時のゲージの作成）
        // 🔴 MeterProvider.ForceFlush は使わない —— 器の OTLP の出口（localhost:4317）が届かず偽を返す。足した読み手だけを集める。
        reader.Collect().Should().BeTrue();
        lock (exporter.Gauges)
            return exporter.Gauges.TryGetValue(RerankStateMetrics.EnabledGaugeName, out var v) ? [.. v] : [];
    }

    // T-559-1: 既定（未設定＝無効）では 0 を出す。最初の検索を待たずに起動時から系列がある。
    [Fact]
    public void 既定では無効として0を収集の出口へ出す()
    {
        Exported(enabled: null).Should().Equal([0L], "既定は無効。このホストの点はちょうど 1 つ。系列が無いと警報が構成を読めない");
    }

    // T-559-2: `Rerank:Enabled=true` では 1 を出す（警報が NFR-29 側へ切り替わる入力）。
    [Fact]
    public void 有効にすると1を収集の出口へ出す()
    {
        Exported(enabled: "true").Should().Equal([1L], "このホストの点はちょうど 1 つで、値は 1");
    }
}
