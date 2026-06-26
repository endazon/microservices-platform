using KnowledgePlatform.Shared.Infrastructure.Extensions;
using LlmGateway.Api.Endpoints;
using LlmGateway.Api.Providers;
using Anthropic.SDK;
using Serilog;

const string ServiceName = "knowledge-platform.llm-gateway";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, logConfig) =>
    logConfig.ConfigureKnowledgePlatformSerilog(ctx.Configuration, ServiceName));

builder.Services.AddKnowledgePlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddKnowledgePlatformAuth(builder.Configuration);
builder.Services.AddKnowledgePlatformHealthChecks();
builder.Services.AddOpenApi();

builder.Services.AddSingleton(_ => new AnthropicClient(
    builder.Configuration["Llm:ApiKey"] ?? "placeholder"));
builder.Services.AddSingleton<ILlmProvider, ClaudeProvider>();

var app = builder.Build();

app.UseKnowledgePlatformMiddleware();
app.MapKnowledgePlatformHealthChecks();
app.MapOpenApi();

CompletionEndpoints.Map(app);
EmbeddingEndpoints.Map(app);

app.Run();

public partial class Program { }
