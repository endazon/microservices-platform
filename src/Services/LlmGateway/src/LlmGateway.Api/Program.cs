using Anthropic.SDK;
using KnowledgePlatform.Shared.Infrastructure.Extensions;
using LlmGateway.Api.Endpoints;
using LlmGateway.Api.Providers;
using LlmGateway.Api.Routing;
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
builder.Services.AddHttpClient();

// FR-11, ADR-0010: 呼び出し先の切り替え（機密区分×ティアの越境マトリクス + 用途別モデル）。
builder.Services.Configure<LlmRoutingOptions>(
    builder.Configuration.GetSection(LlmRoutingOptions.SectionName));
builder.Services.AddSingleton<ILlmRouter, LlmRouter>();

// FR-11: ルーターの判定に従って呼び出し先を切り替えるため、プロバイダをキー付きで登録する。
// ティアB=保護契約済み外部API（Claude）、ティアA=セルフホスト（OSS, 既定は無効エンドポイント）。
builder.Services.AddKeyedSingleton<ILlmProvider, ClaudeProvider>("claude");
builder.Services.AddKeyedSingleton<ILlmProvider, SelfHostedProvider>("selfhosted");

// /embed（埋め込み）は切替対象外。既定プロバイダ（Claude）を用いる。
builder.Services.AddSingleton<ILlmProvider>(sp =>
    sp.GetRequiredKeyedService<ILlmProvider>("claude"));

var app = builder.Build();

app.UseKnowledgePlatformMiddleware();
app.MapKnowledgePlatformHealthChecks();
app.MapOpenApi();

app.MapCompletionEndpoints();
app.MapEmbeddingEndpoints();

app.Run();

public partial class Program { }
