using Anthropic.SDK;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using LlmGateway.Features.Completions;
using LlmGateway.Features.Embeddings.Embed;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Pricing;
using LlmGateway.Domain.Ports;
using LlmGateway.Infrastructure.ExternalServices;
using LlmGateway.Domain.Routing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;

const string ServiceName = "microservices-platform.llm-gateway";

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddPlatformLogging(builder.Configuration, ServiceName);

// FR-02, NFR-09, NFR-16, ADR-0029, ADR-0075, IADR-0379 決定 3, IADR-0397 (#1255): east-west gRPC の
// h2c リスナ（`Grpc:Port`。既定 8081・未設定なら立てない）。HTTP/1.1 の 8080（REST・/health/*・
// introspection）はヘルパが再宣言するので消えない。**readiness は 8080 の /health/ready のまま。**
// AddGrpc() は常に呼ばれる（MapGrpcService は AddGrpc 無しだと起動時に落ちるため、リスナの有無と
// サービス登録の可否を切り離す。TestServer の in-memory HTTP/2 でも gRPC が動く）。
builder.AddPlatformGrpcListener();

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);

// FR-11, NFR, ADR-0006, IADR-0110 (#395): 補完の終了理由（拒否率）を計上する独自 Meter を
// 既存の OTLP パイプラインへ載せる。OpenTelemetry の builder は加算的なので、全サービス共通の
// AddPlatformObservability を変更せずにサービス固有の Meter を追加できる。
builder.Services.AddMetrics();
builder.Services.AddSingleton<LlmCompletionMetrics>();

// FR-10, NFR, ADR-0006, ADR-0044 決定 1・3 (#443): LLM 利用実績（用途別・モデル別のトークン累計と
// 金額換算）。**金額換算は単価表を読む側＝このゲートウェイで行う**（Grafana のクエリに単価を書かない）。
// 単価表は有効期間つきの設定であり、区間の重なり・負値は起動時に落とす（ValidateOnStart）。
builder.Services.AddOptions<ModelPricingOptions>()
    .Bind(builder.Configuration.GetSection(ModelPricingOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ModelPricingOptions>, ModelPricingOptionsValidator>();
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ModelPriceTable>();
builder.Services.AddSingleton<LlmUsageMetrics>();

// Meter は 1 本（サービス名と一致）。補完カウンタと利用実績の計器は同じ Meter に載る。
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(LlmCompletionMetrics.MeterName));
builder.Services.AddPlatformAuth(builder.Configuration);
builder.Services.AddPlatformHealthChecks();
builder.Services.AddOpenApi();

// ADR-0010: Claude SDK (Anthropic.SDK 4.0.0)
// IADR-0114 (AST#290): SDK が解釈できない content ブロック型（thinking 等）で応答全体を失わないよう、
// 応答サニタイズ用の委譲ハンドラを噛ませた HttpClient を渡す。割当モデル（Opus 5 / Sonnet 5）は
// いずれも thinking が既定で有効なため、これが無いと非ストリーミング /complete が全件失敗する。
// ADR-0038 / #850: 割当から Fable 5 を外した（analysis は Opus 5 へ）。ハンドラは引き続き要る。
// 一次ハンドラは既定の HttpClientHandler（システムプロキシ設定は既定で引き継がれる）を使い、
// 応答圧縮だけは SDK 既定の内部クライアントに依存しないよう明示的に有効化する。
builder.Services.AddSingleton(sp => new AnthropicClient(
    new APIAuthentication(builder.Configuration["Llm:ApiKey"] ?? "placeholder"),
    new HttpClient(new AnthropicResponseSanitizingHandler(
        sp.GetRequiredService<ILogger<AnthropicResponseSanitizingHandler>>())
    {
        InnerHandler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        },
    })));
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
// FR-02, FR-03, #992 案 2, [[IADR-0313]]: 決定的ローカル埋め込み（ティアA・プロセス内計算）。
// **既定は appsettings.json で Enabled: false**。使い捨ての統合スタックだけが opt-in する。
builder.Services.AddKeyedSingleton<IEmbeddingProvider, DeterministicEmbeddingProvider>("deterministic-embedding");

// FR-02, FR-05, ADR-0016, IADR-0379 決定 5, IADR-0397 (#1255): 埋め込みの判定器本体。
// REST（/embed）と gRPC（LlmEmbedding/Embed）の**両方がこれを呼ぶ** —— 判定器を 2 つにしない。
builder.Services.AddSingleton<EmbedUseCase>();

// FR-15, ADR-0018, IADR-0029 (#143): 自己申告（イントロスペクション）。段はホストしないが、
// LLM 生成・埋め込みの合成可能ポート（機密区分ルーティングで複数プロバイダを束ねるルータ）を申告する。
builder.Services.AddPlatformIntrospection("llm-gateway", new PipelineOptions(),
    i => i
        .AddPort("llm", nameof(LlmRouter), "claude/selfhosted/copilot")
        .AddPort("embedding", nameof(EmbeddingRouter), "voyage/selfhosted/deterministic"));

var app = builder.Build();

// FR-02, #992, [[IADR-0313]]: 🔴 **決定的ローカル埋め込みが有効なら、起動時に警告を出す。**
// 索引されるベクトルに意味的な近さは無く、**検索品質は保証されない**。
// 有効化は使い捨てのスタックに限る。設定の取り違え（インデックス依存の env 上書き）で
// 本番へ紛れ込んだとき、**ログを見れば分かる**ようにしておく（無言で品質だけが落ちる事故を避ける）。
{
    var embeddingRouting = app.Services.GetRequiredService<IOptions<EmbeddingRoutingOptions>>().Value;
    var deterministic = embeddingRouting.Endpoints
        .FirstOrDefault(e => e.Enabled && e.Provider == "deterministic-embedding");
    if (deterministic is not null)
    {
        app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("LlmGateway.Embed")
            .LogWarning(
                "🔴 決定的ローカル埋め込み '{Endpoint}'（{Model} / {Dimensions}次元 / {Collection}）が有効です。"
                + "表層の文字 3-gram のみで意味的な近さを持たず、検索品質は保証されません。"
                + "使い捨ての検証スタック専用です（#992 / IADR-0313）。",
                deterministic.Name, deterministic.Model, deterministic.Dimensions, deterministic.Collection);
    }
}

app.UsePlatformMiddleware();
app.MapPlatformHealthChecks();
app.MapPlatformIntrospection();
app.MapOpenApi();

app.MapCompletionEndpoints();
app.MapEmbeddingEndpoints();

// FR-02, NFR-09, ADR-0029, ADR-0075, IADR-0379, IADR-0397 (#1255): 埋め込みの gRPC 面。
// `[Authorize(Policy = ServiceCaller)]` を型に持ち、s2s トークン（realm ロール platform-service）だけを通す。
app.MapGrpcService<LlmEmbeddingGrpcService>();

app.Run();

public partial class Program { }
