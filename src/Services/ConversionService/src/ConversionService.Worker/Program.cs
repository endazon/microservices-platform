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

// FR-12: 変換サービス（pandoc ラッパー）
builder.Services.AddSingleton<IConversionService, PandocConversionService>();

// ADR-0003: MassTransit
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<RawDocumentFetchedConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672");
        cfg.ConfigureEndpoints(ctx);
    });
});

var host = builder.Build();
host.Run();
