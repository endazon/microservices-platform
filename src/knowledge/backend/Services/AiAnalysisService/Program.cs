using AiAnalysisService.Common.Observability;
using AiAnalysisService.Features.Analysis;
using AiAnalysisService.Features.Analysis.Analyze;
using AiAnalysisService.Domain.Ports;
using AiAnalysisService.Infrastructure.ExternalServices;
using FluentValidation;
using Platform.Shared.Infrastructure.Foundation.Llm;
using Knowledge.Contracts.Dtos;
using OpenTelemetry.Metrics;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Observability;
using Platform.Shared.Infrastructure.Foundation.Pipeline;

const string ServiceName = "microservices-platform.aianalysis-service";

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddPlatformLogging(builder.Configuration, ServiceName);

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);

// NFR-02, NFR-21, ADR-0006, ADR-0076 決定 5, IADR-0354 (#1204): RAG 回答の初回トークンまでの時間（TTFT）。
// 計画の SLI「初回応答 p95 5 秒」を測る計器はこれまで存在せず、応答完了 p95 を代理値として読んでいた。
// OpenTelemetry の builder は加算的なので、全サービス共通の AddPlatformObservability を変えずに
// サービス固有の Meter（名前はサービス名と一致）を同じ OTLP パイプラインへ載せられる。
builder.Services.AddMetrics();
builder.Services.AddSingleton<RagStreamMetrics>();
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(RagStreamMetrics.MeterName));

builder.Services.AddPlatformAuth(builder.Configuration);
// FR-15, NFR-09, NFR-16, ADR-0029, ADR-0075, [[IADR-0379]] 決定 3, [[IADR-0462]] (#1514, #1255 経路 ⑤):
// east-west gRPC の h2c リスナ（`Grpc:Port`。未設定なら立てない）。面は自己申告の gRPC 面
// （`MapPlatformIntrospection` が REST と対で張る。構成情報 API が宛先ごと opt-in で収集する）。
// HTTP/1.1 のポート（REST・/health/*・introspection）はそのまま残り、readiness も 8080 のままである。
builder.AddPlatformGrpcListener();
// NFR-02, ADR-0044, ADR-0076 決定 4, [[IADR-0378]] (#1203): 合成監視の標識。
// 本サービスは内周なので判定はヘッダで行うが、**LLM を呼ぶ可否**（AllowLlmEgress）をここで受け取る。
builder.Services.AddSyntheticMonitoring(builder.Configuration);
builder.Services.AddPlatformHealthChecks()
    .AddUrlGroup(
        new Uri((builder.Configuration["Services:RetrievalService"] ?? "http://retrieval-service:5003") + "/health/live"),
        "retrieval-service", tags: ["ready"])
    .AddUrlGroup(
        new Uri((builder.Configuration["Services:LlmGateway"] ?? "http://llm-gateway:5007") + "/health/live"),
        "llm-gateway", tags: ["ready"]);
builder.Services.AddOpenApi();

// FR-03, FR-04, FR-05, FR-11, NFR-02, NFR-09, NFR-16, ADR-0029, ADR-0034 決定 1, ADR-0075, 計画 ADR-0086 決定 1,
// ADR-0087 決定 2, 計画 ADR-0089 決定 1, [[IADR-0379]] 決定 4, [[IADR-0400]], [[IADR-0401]] 決定 1, [[IADR-0426]],
// [[IADR-0533]] (#1255): 後段 3 つ（検索・テキスト生成・ABAC スコープ解決）の輸送は **east-west gRPC だけである**。
// ［2026-10-10］REST の並走（名前つき HttpClient `RetrievalService` / `LlmGateway` / `AuthorizationServiceScope`）は撤去した。
// 宛先（`Services:RetrievalServiceGrpc` / `Services:LlmGatewayGrpc` / `Services:AuthorizationServiceGrpc`）が
// 構成されていなければ、各生成クライアントは常に `UNAVAILABLE` を受け取り、それぞれの縮退へ落ちる（[[IADR-0533]] 決定 2）。
// 🔴 **gRPC 輸送は利用者のトークンを転送しない** —— 利用者文脈（user_id / 属性 / action）を
// 要求本文で運び、RetrievalService が受け取った文脈で**自分で** ABAC を解決する（判定の位置は動かない）。
// 🔴 `CompleteStream` は**サーバストリーミング**であり、最初の delta が到着した時点で
// north-south の最初の `token` を書ける —— NFR-02 の SLI（初回トークン）の境界が保たれる。
builder.Services.AddRetrievalSearchGrpcClient(builder.Configuration);
builder.Services.AddSingleton<IRagSearchTransport, GrpcRagSearchTransport>();
builder.Services.AddLlmGatewayGrpcClient(builder.Configuration);
builder.Services.AddSingleton<ILlmCompletionTransport, GrpcLlmCompletionTransport>();
builder.Services.AddAuthzScopeGrpcClient(builder.Configuration);

// FR-04: RAG オーケストレーター
// NFR-02, [[IADR-0378]]: 合成監視の標識（`X-Synthetic-Traffic`）を読むため要求文脈へ触る。
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IRagOrchestrator, RagOrchestrator>();

// FR-07, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 / IADR-0393: 分析依頼の入力検証。
// **アセンブリ走査（AddValidatorsFromAssembly）は使わない** —— 登録が暗黙になり、
// 検証器を消しても起動時には何も起きず、端点が黙って無検証になるためである。
// 1 行 1 検証器の明示登録なら、消したときにコンパイルか DI 解決で止まる。
builder.Services.AddScoped<IValidator<AnalysisTaskRequest>, AnalyzeRequestValidator>();

// FR-15, ADR-0018, IADR-0029 (#143): 自己申告（イントロスペクション）。RAG オーケストレータは
// 他サービスを HTTP で束ねるため合成可能ポートを選択しない。到達可能性とトポロジを与えるため存在申告する。
builder.Services.AddPlatformIntrospection("aianalysis-service", new PipelineOptions());

var app = builder.Build();

app.UsePlatformMiddleware();
app.MapPlatformHealthChecks();
app.MapPlatformIntrospection();
app.MapOpenApi();

app.MapAnalysisEndpoints();

app.Run();

public partial class Program { }
