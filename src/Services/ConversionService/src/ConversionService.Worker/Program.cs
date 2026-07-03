using ConversionService.Worker.Consumers;
using ConversionService.Worker.Services;
using KnowledgePlatform.Shared.Infrastructure.Extensions;
using MassTransit;
using Serilog;

const string ServiceName = "knowledge-platform.conversion-service";

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((sp, logConfig) =>
    logConfig.ConfigureKnowledgePlatformSerilog(builder.Configuration, ServiceName));

builder.Services.AddKnowledgePlatformObservability(builder.Configuration, ServiceName);

// FR-12, ADR-0012: 本文変換（pandoc ラッパー）。
builder.Services.AddSingleton<IBodyConverter, PandocConversionService>();

// FR-12, ADR-0014: 正規化本文・資産のオブジェクトストレージ保管。
builder.Services.AddSingleton<IObjectStore, StorageObjectStore>();

// FR-12, ADR-0012/0010: 図のコード化（LLMゲートウェイ経由、機密区分で送信制御）。
builder.Services.AddHttpClient<IDiagramCoder, LlmGatewayDiagramCoder>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:LlmGateway"] ?? "http://llm-gateway:5007"));

// FR-12, UC-06: 正規化オーケストレータ（本文＋図＋保管を束ねる）。
builder.Services.AddScoped<INormalizationService, NormalizationService>();

// ADR-0003: MassTransit
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<RawDocumentFetchedConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672");

        // FR-12, UC-06 例外フロー: 変換失敗（pandoc エラー・保存失敗）は再試行する。
        // 再試行を使い切った継続失敗は MassTransit が自動で <queue>_error（デッドレター）へ送る。
        cfg.UseMessageRetry(r => r.Intervals(
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)));

        cfg.ConfigureEndpoints(ctx);
    });
});

var host = builder.Build();
host.Run();
