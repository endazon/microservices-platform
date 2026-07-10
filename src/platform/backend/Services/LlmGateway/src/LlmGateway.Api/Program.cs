using Anthropic.SDK;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Extensions;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Introspection;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Pipeline;
using LlmGateway.Api.Foundation.Endpoints;
using LlmGateway.Api.Foundation.Ports;
using LlmGateway.Api.Composable.Adapters;
using LlmGateway.Api.Foundation.Routing;
using Microsoft.Extensions.Options;
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
// Issue #98 レビュー対応: インデックス依存の環境変数上書き（Endpoints__N__Enabled）による取り違えを
// 起動時に fail-fast する（ティア↔プロバイダ整合・既定経路の有効性を EmbeddingRoutingOptionsValidator で検証）。
builder.Services.AddOptions<EmbeddingRoutingOptions>()
    .Bind(builder.Configuration.GetSection(EmbeddingRoutingOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<EmbeddingRoutingOptions>, EmbeddingRoutingOptionsValidator>();
builder.Services.AddSingleton<IEmbeddingRouter, EmbeddingRouter>();
builder.Services.AddKeyedSingleton<IEmbeddingProvider, VoyageEmbeddingProvider>("voyage");
builder.Services.AddKeyedSingleton<IEmbeddingProvider, SelfHostedEmbeddingProvider>("selfhosted-embedding");

// FR-15, ADR-0018, IADR-0029 (#143): 自己申告（イントロスペクション）。段はホストしないが、
// LLM 生成・埋め込みの合成可能ポート（機密区分ルーティングで複数プロバイダを束ねるルータ）を申告する。
builder.Services.AddKnowledgePlatformIntrospection("llm-gateway", new PipelineOptions(),
    i => i
        .AddPort("llm", nameof(LlmRouter), "claude/selfhosted/copilot")
        .AddPort("embedding", nameof(EmbeddingRouter), "voyage/selfhosted"));

var app = builder.Build();

app.UseKnowledgePlatformMiddleware();
app.MapKnowledgePlatformHealthChecks();
app.MapKnowledgePlatformIntrospection();
app.MapOpenApi();

app.MapCompletionEndpoints();
app.MapEmbeddingEndpoints();

app.Run();

public partial class Program { }
