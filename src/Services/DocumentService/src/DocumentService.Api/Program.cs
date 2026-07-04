using DocumentService.Api.Endpoints;
using DocumentService.Api.Infrastructure;
using KnowledgePlatform.Shared.Infrastructure.Extensions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Serilog;

const string ServiceName = "knowledge-platform.document-service";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, logConfig) =>
    logConfig.ConfigureKnowledgePlatformSerilog(ctx.Configuration, ServiceName));

builder.Services.AddKnowledgePlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddKnowledgePlatformAuth(builder.Configuration);
builder.Services.AddKnowledgePlatformHealthChecks()
    .AddNpgSql(
        builder.Configuration.GetConnectionString("DefaultConnection")
            ?? "Host=postgres;Port=5432;Database=document_svc;Username=kp;Password=kp",
        tags: ["ready"])
    .AddRabbitMQ(
        rabbitConnectionString: builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672",
        tags: ["ready"]);
builder.Services.AddOpenApi();

// FR-06: Document DbContext (ADR-0002 Database per Service)
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=postgres;Port=5432;Database=document_svc;Username=kp;Password=kp";
builder.Services.AddDbContext<DocumentDbContext>(opt => opt.UseNpgsql(connStr));

// ADR-0003: MassTransit + RabbitMQ
builder.Services.AddMassTransit(x =>
{
    // FR-01, UC-04: 正規化文書をカタログへ登録する Consumer
    x.AddConsumer<DocumentService.Api.Consumers.DocumentNormalizedConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672");

        // ADR-0003: 正規化文書のカタログ登録（DocumentNormalizedConsumer）の一時的失敗を再試行し、
        // 継続失敗はデッドレターへ退避して回復性を確保する（共通設定）。
        cfg.UseKnowledgePlatformRetry();

        cfg.ConfigureEndpoints(ctx);
    });
});

var app = builder.Build();

// FR-06: 起動時にスキーマを最新 Migration へ更新
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

app.UseKnowledgePlatformMiddleware();
app.MapKnowledgePlatformHealthChecks();
app.MapOpenApi();

app.MapDocumentEndpoints();

app.Run();

public partial class Program { }
