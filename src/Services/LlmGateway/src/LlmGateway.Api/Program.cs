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
// ティアB=保護契約済み外部API（Claude）、ティアA=セルフホスト（OSS, 既定は無効エンドポイント）、
// GitHub Copilot（最難関用途の別経路, ティア確定まで既定は無効エンドポイント）。ADR-0010 / IADR-0022。
builder.Services.AddKeyedSingleton<ILlmProvider, ClaudeProvider>("claude");
builder.Services.AddKeyedSingleton<ILlmProvider, SelfHostedProvider>("selfhosted");
builder.Services.AddKeyedSingleton<ILlmProvider, CopilotProvider>("copilot");

// FR-02, FR-05, ADR-0016, ADR-0017: 埋め込みは LLM 生成とは別系統で機密区分ルーティングする。
// ティアB=Voyage AI（voyage-3.5 / 1024次元・既定）、ティアA=セルフホスト（Ruri v3 / 768次元・既定は無効）。
// confidential/restricted はティアA固定・無効なら fail-closed（EmbeddingRouter）。
builder.Services.Configure<EmbeddingRoutingOptions>(
    builder.Configuration.GetSection(EmbeddingRoutingOptions.SectionName));
builder.Services.AddSingleton<IEmbeddingRouter, EmbeddingRouter>();
builder.Services.AddKeyedSingleton<IEmbeddingProvider, VoyageEmbeddingProvider>("voyage");
builder.Services.AddKeyedSingleton<IEmbeddingProvider, SelfHostedEmbeddingProvider>("selfhosted-embedding");

var app = builder.Build();

app.UseKnowledgePlatformMiddleware();
app.MapKnowledgePlatformHealthChecks();
app.MapOpenApi();

app.MapCompletionEndpoints();
app.MapEmbeddingEndpoints();

app.Run();

public partial class Program { }
