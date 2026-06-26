using KnowledgePlatform.Shared.Infrastructure.Extensions;
using KnowledgePlatform.Bff.Endpoints;
using Serilog;

const string ServiceName = "knowledge-platform.bff";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, logConfig) =>
    logConfig.ConfigureKnowledgePlatformSerilog(ctx.Configuration, ServiceName));

builder.Services.AddKnowledgePlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddKnowledgePlatformAuth(builder.Configuration);
builder.Services.AddKnowledgePlatformHealthChecks()
    .AddRedis(
        builder.Configuration["Redis:ConnectionString"] ?? "redis:6379",
        tags: ["ready"])
    .AddUrlGroup(
        new Uri((builder.Configuration["Services:RetrievalService"] ?? "http://retrieval-service:5003") + "/health/live"),
        "retrieval-service", tags: ["ready"])
    .AddUrlGroup(
        new Uri((builder.Configuration["Services:AiAnalysisService"] ?? "http://aianalysis-service:5004") + "/health/live"),
        "aianalysis-service", tags: ["ready"]);
builder.Services.AddOpenApi();
builder.Services.AddHttpClient();

var app = builder.Build();

app.UseKnowledgePlatformMiddleware();
app.MapKnowledgePlatformHealthChecks();
app.MapOpenApi();

SearchBffEndpoints.Map(app);
AnalysisBffEndpoints.Map(app);

app.Run();

public partial class Program { }
