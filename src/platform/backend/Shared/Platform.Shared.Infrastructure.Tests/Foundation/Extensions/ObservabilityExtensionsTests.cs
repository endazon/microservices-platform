using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Extensions;

// NFR / #901: AddPlatformObservability / AddPlatformLogging が ADR-0006 の前提を満たすことを固定する。
// 本番 13 箇所（それぞれ）が使う、最も広く最も静かな拡張である。
//
// 🔴 **この対象は「壊れても何一つ落ちない」。** OTLP 先が変わってもリソース属性が割れても、
// 例外は出ずログも出続け、HTTP も 200 を返し、ビルドも通る。**テレメトリが黙って別のリソースへ
// 付くだけ**である。したがって「何を観測可能にするか」が試験の設計そのものになる。
//
// 観測できる面は事前に実測して確定させた:
//   - TracerProvider / MeterProvider / **LoggerProvider** の GetResource() —— 三信号すべて読める
//   - OpenTelemetryLoggerOptions の IncludeScopes / IncludeFormattedMessage / ParseStateValues
//   - OtlpEndpointOf（internal のシーム経由。#901 で開けた）
//
// 🔴 観測**できない**面（IADR の「検出しないこと」に対応）:
//   - AddOtlpExporter へ渡した Endpoint が実際に exporter へ届いているか。
//     IOptionsMonitor<OtlpExporterOptions> はどの名前でも既定値を返し、
//     IConfigureOptions<OtlpExporterOptions> の登録数は 0、TracerProviderSdk の
//     リフレクション走査でも URI を持つ Otlp 型は見つからない（3 経路で実測）。
public class ObservabilityExtensionsTests
{
    private const string ServiceName = "test-service";

    private static IConfiguration ConfigWith(string? endpoint) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(endpoint is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { ["Otlp:Endpoint"] = endpoint })
            .Build();

    private static ServiceProvider Build(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddPlatformLogging(config, ServiceName));
        services.AddPlatformObservability(config, ServiceName);
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, object> AttributesOf(Resource resource) =>
        resource.Attributes.ToDictionary(a => a.Key, a => a.Value);

    // --- ADR-0006 の中核: 三信号が同じリソース属性を共有する ---
    //
    // 「相関 ID でログ・トレース・メトリクスを突合する」は、三者のリソース属性が一致していて
    // 初めて成立する。割れても何も落ちないので、ここを試験で固定する。

    [Fact]
    public void トレースのリソースにサービス名とバージョンが載る()
    {
        using var sp = Build(ConfigWith("http://collector:4317"));

        var attrs = AttributesOf(sp.GetRequiredService<TracerProvider>().GetResource());

        attrs.Should().ContainKey("service.name").WhoseValue.Should().Be(ServiceName);
        attrs.Should().ContainKey("service.version").WhoseValue.Should().Be("0.1.0");
    }

    [Fact]
    public void メトリクスのリソースがトレースと一致する()
    {
        using var sp = Build(ConfigWith("http://collector:4317"));

        var trace = AttributesOf(sp.GetRequiredService<TracerProvider>().GetResource());
        var metrics = AttributesOf(sp.GetRequiredService<MeterProvider>().GetResource());

        metrics.Should().BeEquivalentTo(
            trace,
            "リソース属性が割れるとメトリクスとトレースを突合できなくなる（ADR-0006）。割れても何も落ちない");
    }

    [Fact]
    public void ログのリソースがトレースと一致する()
    {
        using var sp = Build(ConfigWith("http://collector:4317"));
        // ログ側のプロバイダは ILoggerFactory を経由して初めて構築される。
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("touch").LogInformation("touch");

        var trace = AttributesOf(sp.GetRequiredService<TracerProvider>().GetResource());
        var logs = AttributesOf(sp.GetRequiredService<LoggerProvider>().GetResource());

        logs.Should().BeEquivalentTo(
            trace,
            "ログのリソースだけ別だと、相関 ID があってもログとトレースが別サービスの記録として並ぶ");
    }

    // --- ADR-0006: 相関 ID が LogRecord へ載る条件 ---
    //
    // 🔴 IncludeScopes=false へ退行すると CorrelationIdMiddleware の BeginScope が LogRecord から
    //    消える。**ログは出続けるので気付けない**（ADR-0006 が静かに成立しなくなる）。
    [Fact]
    public void ログのスコープ取り込みが有効である_相関IDがLogRecordへ載る条件()
    {
        using var sp = Build(ConfigWith("http://collector:4317"));

        var options = sp.GetRequiredService<IOptionsMonitor<OpenTelemetryLoggerOptions>>().CurrentValue;

        options.IncludeScopes.Should().BeTrue(
            "false だと CorrelationIdMiddleware の BeginScope が LogRecord から消える。ログは出続けるので気付けない");
        options.IncludeFormattedMessage.Should().BeTrue();
        options.ParseStateValues.Should().BeTrue(
            "false だと AuditLogger の {AuditAction} 等が LogRecord の属性へ展開されない");
    }

    // --- OTLP 送信先の導出（internal のシーム経由。#901 で開けた） ---
    //
    // 🔴 導出**結果が exporter へ届いているか**は観測できない（クラス冒頭のコメント）。
    //    ここで固定できるのは「設定キーを読むこと」と「既定値」だけである。

    [Fact]
    public void OTLP送信先は設定値を尊重する()
    {
        ObservabilityExtensions.OtlpEndpointOf(ConfigWith("http://collector-a:4317"))
            .Should().Be(new Uri("http://collector-a:4317"));
    }

    [Fact]
    public void OTLP送信先は設定が無ければ既定値へ落ちる()
    {
        ObservabilityExtensions.OtlpEndpointOf(ConfigWith(null))
            .Should().Be(
                new Uri("http://otel-collector:4317"),
                "既定値を書き換えると、設定を持たない環境のテレメトリが黙って別の宛先へ行く");
    }

    // --- 適用前の既定値（IADR-0233 / U4 の作法）---
    //
    // 拡張を呼ばなければ TracerProvider は解決できない。これが無いと
    // 「ヘルパが何もしなくても、たまたま既定で解決できる」形の緑を見分けられない。
    [Fact]
    public void 適用前は各プロバイダが解決できない()
    {
        using var bare = new ServiceCollection().AddLogging().BuildServiceProvider();

        bare.GetService<TracerProvider>().Should().BeNull();
        bare.GetService<MeterProvider>().Should().BeNull();
    }
}
