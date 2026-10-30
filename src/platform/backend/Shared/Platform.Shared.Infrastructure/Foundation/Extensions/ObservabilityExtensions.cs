using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Shared.Infrastructure.Foundation.Messaging;
using Platform.Shared.Infrastructure.Foundation.Middleware;

namespace Platform.Shared.Infrastructure.Foundation.Extensions;

public static class ObservabilityExtensions
{
    private const string ServiceVersion = "0.1.0";
    private const string DefaultOtlpEndpoint = "http://otel-collector:4317";

    // NFR-19, [[IADR-0649]] (#2129): Wolverine の ActivitySource の名前（`Wolverine.Runtime.WolverineTracing.ActivitySource`）。
    // 型は internal なので名前を写す。写し間違えは TraceContinuityTests が Wolverine の実物と突き合わせて捕まえる。
    public const string WolverineActivitySourceName = "Wolverine";

    // ADR-0006, IADR-0216: ログ・トレース・メトリクスは同じ OTLP 先と同じリソース属性を共有する。
    // 「相関 ID で三者を突合する」（ADR-0006 §決定）はリソース属性が一致していて初めて成立するため、
    // 導出を 2 箇所に書かず、この 2 つのヘルパへ集約する。
    // 🔴 **試験用に internal で開けている**（#901 / private ではない）。
    // OTLP の送信先は AddOtlpExporter(o => o.Endpoint = ...) の中に閉じてしまい、
    // 外から一切観測できない —— IOptionsMonitor<OtlpExporterOptions> はどの名前でも既定値
    // （http://localhost:4317）を返し、IConfigureOptions<OtlpExporterOptions> の登録数は 0、
    // TracerProviderSdk をリフレクションで走査しても URI を持つ Otlp 型は見つからない（3 経路で実測）。
    // そのため「設定キーを変えた」「既定値を書き換えた」という退行が**まったく検出できない**。
    // 導出だけでも観測可能にするため、ここを internal にして直接試験する。
    // **残る観測不能領域**: 下の戻り値が実際に exporter へ届いているかは依然として証明できない
    // （IADR の「検出しないこと」を参照）。
    internal static Uri OtlpEndpointOf(IConfiguration config) =>
        new(config["Otlp:Endpoint"] ?? DefaultOtlpEndpoint);

    private static ResourceBuilder ResourceOf(string serviceName) =>
        ResourceBuilder.CreateDefault().AddService(serviceName, serviceVersion: ServiceVersion);

    // ADR-0006: OTel/Prometheus/Loki/Tempo への統一計装
    public static IServiceCollection AddPlatformObservability(
        this IServiceCollection services,
        IConfiguration config,
        string serviceName)
    {
        var otlpEndpoint = OtlpEndpointOf(config);
        var resourceBuilder = ResourceOf(serviceName);

        services.AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .SetResourceBuilder(resourceBuilder)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                // NFR-19, ADR-0006, [[IADR-0649]] (#2129): メッセージの送受信もトレースに載せる。聞き手が居ないと Wolverine は
                // 受信側の Activity を作らず、エンベロープが運ぶ親（traceparent）が使われない ——
                // コンシューマのスパン・ログが HTTP の要求のトレースから切れ、相関がメッセージの 1 ホップ目で途切れる。
                .AddSource(WolverineActivitySourceName)
                .AddOtlpExporter(o => o.Endpoint = otlpEndpoint))
            .WithMetrics(metrics => metrics
                .SetResourceBuilder(resourceBuilder)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                // #1640: 受け口の外への呼び出しの時間切れ（`ConsumerTimeoutMetrics`）。全サービス共通の Meter。
                // #2043 / IADR-0592: 自動再試行を使い切ったデッドレター到達（`DeadLetterMetrics`）も同じ Meter に載る。
                .AddMeter(ConsumerTimeoutMetrics.MeterName)
                .AddOtlpExporter(o => o.Endpoint = otlpEndpoint));

        // NFR-19, ADR-0006, [[IADR-0649]] (#2129): 受けた要求の相関 ID を、IHttpClientFactory のすべてのクライアントで後段へ引き継ぐ。
        services.AddHttpContextAccessor();
        services.TryAddTransient<CorrelationIdForwardingHandler>();
        services.ConfigureHttpClientDefaults(http => http.AddHttpMessageHandler<CorrelationIdForwardingHandler>());

        return services;
    }

    // ADR-0030, IADR-0216: ロギングは標準 ILogger + OpenTelemetry Logs に一本化する（Serilog は不採用）。
    // 旧 ConfigurePlatformSerilog の置換。UseSerilog が全プロバイダを置換していたのと異なり、これは
    // 既定の Console プロバイダへ「追加」する形であり、コンソール出力は標準の書式で残る。
    public static ILoggingBuilder AddPlatformLogging(
        this ILoggingBuilder logging,
        IConfiguration config,
        string serviceName)
    {
        var otlpEndpoint = OtlpEndpointOf(config);
        var resourceBuilder = ResourceOf(serviceName);

        logging.AddOpenTelemetry(options =>
        {
            options.SetResourceBuilder(resourceBuilder);
            // ADR-0006: 相関 ID の突合。CorrelationIdMiddleware の BeginScope を LogRecord の属性へ載せる。
            options.IncludeScopes = true;
            options.IncludeFormattedMessage = true;
            // 構造化ログの項目（AuditLogger の {AuditAction} 等）を LogRecord の属性へ展開する。
            options.ParseStateValues = true;
            options.AddOtlpExporter(o => o.Endpoint = otlpEndpoint);
        });

        // FR-12, NFR-21, IADR-0609 決定 2, [[IADR-0656]] (#2178): Wolverine（JasperFx の RetryBlock）のメモリ上の再試行の失敗を計器にする
        // （警報 MessagingRetryBlockErrors の元）。ログは書かず、テンプレートの照合に当たった記録だけを数える。
        logging.AddPlatformRetryBlockErrorMetrics();

        return logging;
    }
}
