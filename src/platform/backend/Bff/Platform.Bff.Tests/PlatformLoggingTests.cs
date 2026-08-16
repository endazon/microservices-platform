using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Platform.Shared.Infrastructure.Foundation.Audit;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Middleware;

namespace Platform.Bff.Tests;

// ADR-0006, ADR-0030, IADR-0216: ログの出口を Serilog から OTel Logging SDK へ移した。
// IADR-0216 §決定 2 が「変わらない」と宣言した項目（構造化ログの項目・CorrelationId の伝播・
// 監査ログの出口・OTLP 先が不在でも起動が落ちないこと）を、宣言ではなく実測で裏取りする。
public class PlatformLoggingTests
{
    /// <summary>
    /// LogRecord はプールで再利用されるため、OnEnd の時点で必要な値を素の型へ写し取る。
    /// </summary>
    private sealed record CapturedLog(
        string? Body,
        LogLevel Level,
        Dictionary<string, object?> Attributes,
        Dictionary<string, object?> Scopes);

    private sealed class CapturingProcessor : BaseProcessor<LogRecord>
    {
        public List<CapturedLog> Records { get; } = [];

        public override void OnEnd(LogRecord data)
        {
            var attributes = new Dictionary<string, object?>();
            if (data.Attributes is not null)
            {
                foreach (var kv in data.Attributes)
                    attributes[kv.Key] = kv.Value;
            }

            var scopes = new Dictionary<string, object?>();
            data.ForEachScope(
                (scope, state) =>
                {
                    foreach (var kv in scope)
                        state[kv.Key] = kv.Value;
                },
                scopes);

            Records.Add(new CapturedLog(data.FormattedMessage, data.LogLevel, attributes, scopes));
        }
    }

    /// <summary>
    /// 本番と同じ AddPlatformLogging を通したうえで、OTLP エクスポータの手前に捕捉用プロセッサを挿す。
    /// OTLP 先（既定 http://otel-collector:4317）はテスト環境に存在しないため、
    /// 「エクスポート先が居なくてもログが流れる」ことの確認も兼ねる。
    /// </summary>
    private static (ServiceProvider Provider, CapturingProcessor Capture) BuildLogging(
        IDictionary<string, string?>? settings = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();

        var capture = new CapturingProcessor();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddPlatformLogging(config, "microservices-platform.test");
            logging.AddOpenTelemetry(o => o.AddProcessor(capture));
        });

        return (services.BuildServiceProvider(), capture);
    }

    // IADR-0216 §決定 2: ログが実際に LogRecord として出口へ流れること（最低限の生存確認）。
    [Fact]
    public void AddPlatformLogging_ログを_LogRecord_として出力する()
    {
        var (provider, capture) = BuildLogging();
        using (provider)
        {
            var logger = provider.GetRequiredService<ILogger<PlatformLoggingTests>>();

            logger.LogInformation("ログの出口の疎通確認 {Marker}", "otel-logging");

            provider.GetRequiredService<ILoggerFactory>().Dispose();
        }

        Assert.NotEmpty(capture.Records);
        var record = Assert.Single(capture.Records, r => r.Attributes.ContainsKey("Marker"));
        Assert.Equal("otel-logging", record.Attributes["Marker"]);
        // IncludeFormattedMessage = true により整形済み本文が Body に載る。
        Assert.Equal("ログの出口の疎通確認 otel-logging", record.Body);
        Assert.Equal(LogLevel.Information, record.Level);
    }

    // IADR-0216 §決定 2「CorrelationId の伝播」: Serilog の LogContext.PushProperty を
    // ILogger.BeginScope へ移しても、相関 ID が LogRecord に載り続けること（ADR-0006 の三者突合）。
    [Fact]
    public async Task CorrelationIdMiddleware_相関IDをスコープとして_LogRecord_へ載せる()
    {
        var (provider, capture) = BuildLogging();
        const string incoming = "correlation-id-under-test";

        using (provider)
        {
            var middleware = new CorrelationIdMiddleware(
                next: _ =>
                {
                    // ミドルウェアが張ったスコープの内側で出したログ。
                    provider.GetRequiredService<ILogger<PlatformLoggingTests>>()
                        .LogWarning("スコープ内のログ {Inner}", "inside");
                    return Task.CompletedTask;
                },
                logger: provider.GetRequiredService<ILogger<CorrelationIdMiddleware>>());

            var context = new DefaultHttpContext();
            context.Request.Headers["X-Correlation-ID"] = incoming;

            await middleware.InvokeAsync(context);

            // レスポンスヘッダの扱いは Serilog 時代から変えていない。
            Assert.Equal(incoming, context.Response.Headers["X-Correlation-ID"]);

            provider.GetRequiredService<ILoggerFactory>().Dispose();
        }

        var record = Assert.Single(capture.Records, r => r.Attributes.ContainsKey("Inner"));
        // IncludeScopes = true により BeginScope の項目が LogRecord のスコープとして読める。
        Assert.True(
            record.Scopes.ContainsKey("CorrelationId"),
            $"CorrelationId がスコープに無い。実際のスコープ: [{string.Join(", ", record.Scopes.Keys)}]");
        Assert.Equal(incoming, record.Scopes["CorrelationId"]);
    }

    // IADR-0216 §決定 2「監査ログ（AuditLogger）の出口」: docs/security/security.md が約束する
    // 「Audit=true を構造化プロパティに付与し、可観測性基盤で監査として抽出可能」が保たれること。
    // FR-15, ADR-0004 の監査ログがこの写像に依存する。
    [Fact]
    public void AuditLogger_監査プロパティが_LogRecord_の属性として保たれる()
    {
        var (provider, capture) = BuildLogging();
        using (provider)
        {
            var auditLogger = new AuditLogger(provider.GetRequiredService<ILogger<AuditLogger>>());

            auditLogger.Record("config.read", "tester", "granted", "unit-test");

            provider.GetRequiredService<ILoggerFactory>().Dispose();
        }

        var record = Assert.Single(capture.Records, r => r.Attributes.ContainsKey("AuditAction"));
        // ParseStateValues = true により構造化プロパティが LogRecord の属性へ展開される。
        Assert.Equal("config.read", record.Attributes["AuditAction"]);
        Assert.Equal("tester", record.Attributes["AuditSubject"]);
        Assert.Equal("granted", record.Attributes["AuditOutcome"]);
        Assert.Equal("unit-test", record.Attributes["AuditDetail"]);
        // 監査の抽出条件そのもの。ここが落ちると security.md の約束が静かに壊れる。
        Assert.Equal(true, record.Attributes["Audit"]);
    }

    // IADR-0216 §決定 2「OTLP 先が不在でも起動が落ちないこと」: エクスポータの接続失敗は
    // ログに出るが例外へ昇格しない。到達不能なエンドポイントを明示して実測する。
    [Fact]
    public async Task OTLP先が不在でもホストは起動しログを出せる()
    {
        var builder = WebApplication.CreateBuilder();
        // 任意の空きポートで listen する（テスト間のポート衝突を避ける）。
        builder.Configuration["Urls"] = "http://127.0.0.1:0";
        // 誰も listen していないポート。名前解決に頼らないよう loopback を指定する。
        builder.Configuration["Otlp:Endpoint"] = "http://127.0.0.1:1";
        builder.Logging.AddPlatformLogging(builder.Configuration, "microservices-platform.test-host");

        await using var app = builder.Build();

        // 起動が例外で落ちないこと。
        await app.StartAsync();
        app.Logger.LogInformation("OTLP 先が不在でも出せるログ {Marker}", "unreachable-otlp");
        await app.StopAsync();
    }
}
