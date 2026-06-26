using KnowledgePlatform.Shared.Infrastructure.Extensions;
using RetrievalService.Api.Endpoints;
using Serilog;

const string ServiceName = "knowledge-platform.retrieval-service";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, logConfig) =>
    logConfig.ConfigureKnowledgePlatformSerilog(ctx.Configuration, ServiceName));

builder.Services.AddKnowledgePlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddKnowledgePlatformAuth(builder.Configuration);
builder.Services.AddKnowledgePlatformHealthChecks()
    .AddNpgSql(
        builder.Configuration.GetConnectionString("DefaultConnection")
            ?? "Host=postgres;Port=5432;Database=retrieval_svc;Username=kp;Password=kp",
        tags: ["ready"]);
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseKnowledgePlatformMiddleware();
app.MapKnowledgePlatformHealthChecks();
app.MapOpenApi();

SearchEndpoints.Map(app);

app.Run();

public partial class Program { }
