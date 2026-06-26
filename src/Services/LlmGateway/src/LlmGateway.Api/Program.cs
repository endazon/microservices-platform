using Anthropic.SDK;
using KnowledgePlatform.Shared.Infrastructure.Extensions;
using LlmGateway.Api.Endpoints;
using LlmGateway.Api.Providers;
using Serilog;

const string ServiceName = "knowledge-platform.llm-gateway";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, logConfig) =>
    logConfig.ConfigureKnowledgePlatformSerilog(ctx.Configuration, ServiceName));

builder.Services.AddKnowledgePlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddKnowledgePlatformAuth(builder.Configuration);
builder.Services.AddKnowledgePlatformHealthChecks();
builder.Services.AddOpenApi();

// ADR-0010: Claude SDK (Anthropic.SDK 4.0.0)
builder.Services.AddSingleton(_ => new AnthropicClient(
    builder.Configuration["Llm:ApiKey"] ?? "placeholder"));
builder.Services.AddSingleton<ILlmProvider, ClaudeProvider>();

var app = builder.Build();

app.UseKnowledgePlatformMiddleware();
app.MapKnowledgePlatformHealthChecks();
app.MapOpenApi();

app.MapCompletionEndpoints();
app.MapEmbeddingEndpoints();

app.Run();

public partial class Program { }
