using OpenTelemetry.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Platform.Shared.Infrastructure.Foundation.Introspection;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Introspection;

// FR-15, NFR-16, ADR-0029, IADR-0379 決定 4, IADR-0462 (#1514), [[IADR-0533]] 決定 3 (#1517):
// 構成情報 API の登録（`AddPlatformConfigInspection`）が gRPC だけの収集器を組み、撤去した旧キーで起動を止めることを固定する。
//
// ［2026-10-10 / #1517］REST の収集は撤去した。宛先は `Introspection:Services`（値は h2c のアドレス）であり、
// gRPC の収集器と s2s トークンの発行側は**常に**登録する（従前は `Introspection:GrpcServices` が在るときだけだった）。
public class ConfigInspectionGrpcRegistrationTests
{
    private static IServiceProvider Build(Dictionary<string, string?> config)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(config);
        builder.AddPlatformConfigInspection();
        return builder.Services.BuildServiceProvider();
    }

    // 収集器は gRPC の収集器の上に組まれ、s2s の発行側が解決できる。
    [Fact]
    public void Registers_the_grpc_collector_and_service_token()
    {
        var sp = Build(new()
        {
            ["Introspection:Services:document-service"] = "http://document-service:8081",
            ["ServiceToken:ClientId"] = "bff",
            ["ServiceToken:ClientSecret"] = "x",
        });

        sp.GetRequiredService<IEffectiveConfigCollector>().Should().BeOfType<EffectiveConfigCollector>();
        sp.GetService<GrpcServiceIntrospectionCollector>().Should().NotBeNull();
        sp.GetService<IServiceTokenProvider>().Should().NotBeNull();
    }

    // FR-15, NFR-21, [[IADR-0652]] (#2143): ドリフトの計器が登録され、その Meter が OpenTelemetry の収集対象に入る
    // （AddMeter が無いと計器は在っても OTLP へ出ず、警報 ConfigDriftDetected は永久に鳴らない）。
    [Fact]
    public void Registers_the_drift_metrics_and_collects_its_meter()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceToken:ClientId"] = "bff",
            ["ServiceToken:ClientSecret"] = "x",
        });
        builder.AddPlatformConfigInspection();
        // 本番の読み手（OTLP の出口。AddPlatformObservability が足す）の代わりに、何もしない読み手を 1 つ置く
        // （読み手が無いと MeterProvider は計器を購読しない）。
        builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddReader(
            new OpenTelemetry.Metrics.BaseExportingMetricReader(new NullMetricExporter())));
        var sp = builder.Services.BuildServiceProvider();
        // 収集側（OpenTelemetry の MeterProvider）を先に起こす。AddMeter に名前があれば、計器はこれに購読されて Enabled になる。
        _ = sp.GetRequiredService<OpenTelemetry.Metrics.MeterProvider>();

        System.Diagnostics.Metrics.Instrument? gauge = null;
        using var probe = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Name == ConfigDriftMetrics.FindingsGaugeName) gauge = instrument;
            },
        };
        probe.Start();
        _ = sp.GetRequiredService<ConfigDriftMetrics>();

        gauge.Should().NotBeNull();
        gauge!.Enabled.Should().BeTrue("収集の宣言（AddMeter）が無いと計器は OTLP へ出ず、警報は永久に鳴らない");
    }

    // 🔴 撤去した旧キー `Introspection:GrpcServices` が残っていれば起動を止める（黙って無視しない）。
    [Theory]
    [InlineData("http://document-service:8081")]
    [InlineData("")]
    public void Retired_grpc_services_key_fails_fast(string value)
    {
        var act = () => Build(new()
        {
            ["Introspection:Services:document-service"] = "http://document-service:8081",
            ["Introspection:GrpcServices:document-service"] = value,
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Introspection:GrpcServices*Introspection:Services*document-service*");
    }

    // 対照: 旧キーが無ければ起動する（宛先が 0 件でも組み立てられる）。
    [Fact]
    public void Without_the_retired_key_it_builds()
    {
        var act = () => Build(new());

        act.Should().NotThrow();
    }

    private sealed class NullMetricExporter : OpenTelemetry.BaseExporter<OpenTelemetry.Metrics.Metric>
    {
        public override OpenTelemetry.ExportResult Export(in OpenTelemetry.Batch<OpenTelemetry.Metrics.Metric> batch) =>
            OpenTelemetry.ExportResult.Success;
    }
}
