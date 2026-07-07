using FeedbackService.Api.Foundation.Endpoints;
using FeedbackService.Api.Foundation.Persistence;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Extensions;
using Microsoft.EntityFrameworkCore;
using Serilog;

const string ServiceName = "knowledge-platform.feedback-service";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, logConfig) =>
    logConfig.ConfigureKnowledgePlatformSerilog(ctx.Configuration, ServiceName));

builder.Services.AddKnowledgePlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddKnowledgePlatformAuth(builder.Configuration);
builder.Services.AddKnowledgePlatformHealthChecks()
    .AddNpgSql(
        builder.Configuration.GetConnectionString("DefaultConnection")
            ?? "Host=postgres;Port=5432;Database=feedback_svc;Username=kp;Password=kp",
        tags: ["ready"]);
builder.Services.AddOpenApi();

// FR-08: Feedback DbContext（DB-per-service, ADR-0002）
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=postgres;Port=5432;Database=feedback_svc;Username=kp;Password=kp";
builder.Services.AddDbContext<FeedbackDbContext>(opt => opt.UseNpgsql(connStr));

var app = builder.Build();

// FR-08: 起動時にスキーマを最新 Migration へ更新
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FeedbackDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

app.UseKnowledgePlatformMiddleware();
app.MapKnowledgePlatformHealthChecks();
app.MapOpenApi();

app.MapFeedbackEndpoints();

app.Run();

public partial class Program { }
