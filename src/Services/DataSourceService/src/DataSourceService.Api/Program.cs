using DataSourceService.Api.Foundation.Endpoints;
using DataSourceService.Api.Foundation.Persistence;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Extensions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Serilog;

const string ServiceName = "knowledge-platform.datasource-service";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, logConfig) =>
    logConfig.ConfigureKnowledgePlatformSerilog(ctx.Configuration, ServiceName));

builder.Services.AddKnowledgePlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddKnowledgePlatformAuth(builder.Configuration);
builder.Services.AddKnowledgePlatformHealthChecks()
    .AddNpgSql(
        builder.Configuration.GetConnectionString("DefaultConnection")
            ?? "Host=postgres;Port=5432;Database=datasource_svc;Username=kp;Password=kp",
        tags: ["ready"])
    .AddRabbitMQ(
        rabbitConnectionString: builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672",
        tags: ["ready"]);
builder.Services.AddOpenApi();

// FR-01: DataSource DbContext
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=postgres;Port=5432;Database=datasource_svc;Username=kp;Password=kp";
builder.Services.AddDbContext<DataSourceDbContext>(opt => opt.UseNpgsql(connStr));

// ADR-0003: MassTransit
builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672");
        cfg.ConfigureEndpoints(ctx);
    });
});

var app = builder.Build();

// FR-01: 起動時にスキーマを最新 Migration へ更新
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DataSourceDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

app.UseKnowledgePlatformMiddleware();
app.MapKnowledgePlatformHealthChecks();
app.MapOpenApi();

app.MapDataSourceEndpoints();

app.Run();

public partial class Program { }
