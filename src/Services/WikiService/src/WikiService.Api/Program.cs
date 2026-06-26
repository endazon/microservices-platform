using KnowledgePlatform.Shared.Infrastructure.Extensions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Serilog;
using WikiService.Api.Consumers;
using WikiService.Api.Endpoints;
using WikiService.Api.Infrastructure;

const string ServiceName = "knowledge-platform.wiki-service";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, logConfig) =>
    logConfig.ConfigureKnowledgePlatformSerilog(ctx.Configuration, ServiceName));

builder.Services.AddKnowledgePlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddKnowledgePlatformAuth(builder.Configuration);
builder.Services.AddKnowledgePlatformHealthChecks()
    .AddNpgSql(
        builder.Configuration.GetConnectionString("DefaultConnection")
            ?? "Host=postgres;Port=5432;Database=wiki_svc;Username=kp;Password=kp",
        tags: ["ready"])
    .AddRabbitMQ(
        rabbitConnectionString: builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672",
        tags: ["ready"]);
builder.Services.AddOpenApi();

// FR-13: Wiki DbContext
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=postgres;Port=5432;Database=wiki_svc;Username=kp;Password=kp";
builder.Services.AddDbContext<WikiDbContext>(opt => opt.UseNpgsql(connStr));

// ADR-0003: MassTransit — DocumentUpdated を購読し Wiki ページに同期
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<DocumentSyncConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672");
        cfg.ConfigureEndpoints(ctx);
    });
});

var app = builder.Build();

// FR-13: 起動時にスキーマを最新 Migration へ更新
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

app.UseKnowledgePlatformMiddleware();
app.MapKnowledgePlatformHealthChecks();
app.MapOpenApi();

app.MapWikiEndpoints();

app.Run();

public partial class Program { }
