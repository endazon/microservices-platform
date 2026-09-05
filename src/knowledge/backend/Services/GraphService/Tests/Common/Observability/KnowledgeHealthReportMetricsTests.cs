using System.Diagnostics.Metrics;
using System.Net;
using AwesomeAssertions;
using GraphService.Common.Observability;
using GraphService.Domain.Ports;
using GraphService.Infrastructure.ExternalServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphService.Tests.Common.Observability;

// FR-10, NFR-21, SC-10, ADR-0006, ADR-0076 決定 3, [[IADR-0370]], [[IADR-0389]] 決定 5 (#1246):
// **生産者が生きていることを示す系列**。`absent_over_time` アラートの土台である。
//
// 🔴 **なぜ必要か。** 収集が止まっても受け口の件数は 0 にならない —— 全量スナップショット置換
// なので、**最後に届いた値のまま凍る。** 画面上は「安定している」に見え、**沈黙が正常と読める。**
// 件数ではなく「届けた回数の系列」を出し、その不在を鳴らす。
//
// 🔴 **受理されたときだけ数える。** 試みた回数を数えると、受け口が死んでいる間も系列が
// 生き続け、`absent` が沈黙する。送出は fail-open（到達できなくても続行する）なので、
// この区別が無いとアラートは何も検知しない。
[Trait("TestKind", "Unit")]
public class KnowledgeHealthReportMetricsTests
{
    // 実行時の名前は**アラート式（deploy/prometheus/alerts.yml）が書いている文字列**である。
    // 変えるなら両方 —— 片方だけ変えると、式は永久に空ベクタを返して発火しない（#1110 の型）。
    [Fact]
    public void 計器の名前とタグはアラート式と同じ綴りである()
    {
        KnowledgeHealthReportMetrics.ReportCounterName.Should().Be("knowledge.health.report.total",
            "Prometheus 側では knowledge_health_report_total になる");
        KnowledgeHealthReportMetrics.IndicatorTag.Should().Be("knowledge.indicator",
            "Prometheus 側では knowledge_indicator になる");
        KnowledgeHealthReportMetrics.MeterName.Should().Be(EdgeTypeFallbackMetrics.MeterName,
            "同じ Meter に載せる（Program.cs の AddMeter を増やさない）");
    }

    [Fact]
    public async Task 受け口が受理したら指標名つきで1件数える()
    {
        var (metrics, probe) = NewProbe();
        var reporter = new HttpKnowledgeHealthReporter(
            new SingleClientFactory(new StubHandler(HttpStatusCode.Accepted)),
            metrics, NullLogger<HttpKnowledgeHealthReporter>.Instance);

        await reporter.ReportAsync("unresolved-links", [], ct: TestContext.Current.CancellationToken);

        probe.Measurements.Should().ContainSingle();
        probe.Measurements[0].Value.Should().Be(1);
        probe.Measurements[0].Indicator.Should().Be("unresolved-links");
    }

    // 🔴 **陰性。受け口がエラーを返したら数えない。**
    // ここが数えてしまうと、受け口が壊れている間も系列が生き続けて不在が鳴らない。
    [Fact]
    public async Task 受け口がエラーを返したら数えない()
    {
        var (metrics, probe) = NewProbe();
        var reporter = new HttpKnowledgeHealthReporter(
            new SingleClientFactory(new StubHandler(HttpStatusCode.InternalServerError)),
            metrics, NullLogger<HttpKnowledgeHealthReporter>.Instance);

        await reporter.ReportAsync("unresolved-links", [], ct: TestContext.Current.CancellationToken);

        probe.Measurements.Should().BeEmpty();
    }

    // 到達できない場合（fail-open で握る経路）も数えない。
    [Fact]
    public async Task 受け口へ到達できなければ数えない()
    {
        var (metrics, probe) = NewProbe();
        var reporter = new HttpKnowledgeHealthReporter(
            new SingleClientFactory(new ThrowingHandler()),
            metrics, NullLogger<HttpKnowledgeHealthReporter>.Instance);

        await reporter.ReportAsync("edge-type-usage", [], ct: TestContext.Current.CancellationToken);

        probe.Measurements.Should().BeEmpty("送出は fail-open だが、届いていない事実は残す");
    }

    // ── 器 ────────────────────────────────────────────────────────

    private static (KnowledgeHealthReportMetrics Metrics, CounterProbe Probe) NewProbe()
    {
        var factory = new ServiceCollection().AddMetrics().BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();
        var metrics = new KnowledgeHealthReportMetrics(factory);
        return (metrics, new CounterProbe());
    }

    // 実行時の計測を拾う。**Meter 名と計器名で絞る**（他サービスの計器を巻き込まない）。
    private sealed class CounterProbe : IDisposable
    {
        public List<(long Value, string? Indicator)> Measurements { get; } = [];
        private readonly MeterListener _listener;

        public CounterProbe()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == KnowledgeHealthReportMetrics.MeterName
                        && instrument.Name == KnowledgeHealthReportMetrics.ReportCounterName)
                        listener.EnableMeasurementEvents(instrument);
                },
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                string? indicator = null;
                foreach (var tag in tags)
                    if (tag.Key == KnowledgeHealthReportMetrics.IndicatorTag)
                        indicator = tag.Value?.ToString();
                Measurements.Add((value, indicator));
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("受け口へ到達できない");
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://dashboard-service") };
    }
}
