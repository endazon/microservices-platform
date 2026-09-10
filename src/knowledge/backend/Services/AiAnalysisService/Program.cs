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

// FR-04: HTTP クライアント設定（サービス間通信）
builder.Services.AddPlatformAuthzScopeHttpClient(builder.Configuration);
builder.Services.AddHttpClient("RetrievalService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:RetrievalService"]
        ?? "http://retrieval-service:5003"));
// FR-03, FR-04, FR-05, NFR-09, NFR-16, ADR-0029, ADR-0034 決定 1, ADR-0075, 計画 ADR-0086 決定 1,
// ADR-0087 決定 2, ADR-0089 決定 1, [[IADR-0379]] 決定 4・5, [[IADR-0425]] (#1255):
// RAG の検索の輸送。**並走中の正は REST である。** `Services:RetrievalServiceGrpc`（h2c の
// アドレス）が構成されたときだけ生成クライアントが登録され、そのときに限り gRPC 輸送を使う。
// 無ければ上の名前つき HttpClient で REST のまま（戻すのは構成を外すだけ。コードは変えない）。
// 🔴 **gRPC 輸送は利用者のトークンを転送しない** —— 利用者文脈（user_id / 属性 / action）を
// 要求本文で運び、RetrievalService が受け取った文脈で**自分で** ABAC を解決する
// （判定の位置は動かない。`ADR-0086` 実装側残作業 2 の実体）。
// 🔴 **前提**: 呼び出し先に `Services:GraphServiceGrpc` が在ること。無いと二段検索の近傍展開は
// REST 実装のままであり、転送できる利用者の資格情報が無いので**呼ばずに警告**する
// （グラフ再ランクが効かない。helm・compose のどちらにも既に在る）。
builder.Services.AddRetrievalSearchGrpcClient(builder.Configuration);
if (!string.IsNullOrWhiteSpace(builder.Configuration[RetrievalSearchGrpcClientExtensions.AddressKey]))
    builder.Services.AddSingleton<IRagSearchTransport, GrpcRagSearchTransport>();
// 🔴 NFR-09, ADR-0084 決定 1, [[IADR-0424]] (#1364): **REST 面は `ServiceCaller` を要する。**
// 呼び出し側サービス自身の s2s トークンを載せる（利用者のトークンは載せない）。
builder.Services.AddHttpClient("LlmGateway", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:LlmGateway"]
        ?? "http://llm-gateway:5007"))
    .AddLlmGatewayServiceToken(builder.Configuration);

// FR-04, FR-11, NFR-02, NFR-09, NFR-16, ADR-0029, ADR-0075, IADR-0379 決定 5, IADR-0400 (#1255):
// テキスト生成の輸送。**並走中の正は REST である。** `Services:LlmGatewayGrpc`（h2c のアドレス）が
// 構成されたときだけ生成クライアントが登録され、そのときに限り gRPC 輸送を使う。無ければ REST 輸送
// （上の名前つき HttpClient を使う HttpLlmCompletionTransport）のまま。戻すのは構成を外すだけでよい。
//
// 🔴 `CompleteStream` は**サーバストリーミング**であり、最初の delta が到着した時点で
// north-south の最初の `token` を書ける —— NFR-02 の SLI（初回トークン）の境界が保たれる。
builder.Services.AddLlmGatewayGrpcClient(builder.Configuration);
if (!string.IsNullOrWhiteSpace(builder.Configuration[LlmGatewayGrpcClientExtensions.AddressKey]))
    builder.Services.AddSingleton<ILlmCompletionTransport, GrpcLlmCompletionTransport>();
else
    builder.Services.AddSingleton<ILlmCompletionTransport, HttpLlmCompletionTransport>();

// FR-05, NFR-09, NFR-16, ADR-0004, ADR-0029, ADR-0075, IADR-0379 決定 5, IADR-0401 決定 1 (#1255):
// ABAC スコープ解決の gRPC 経路。**並走中の正は REST である。**
// `Services:AuthorizationServiceGrpc`（h2c のアドレス）が構成されたときだけ `AuthzScopeGrpcClient` が
// 登録され、RagOrchestrator は在ればそれを使う（無ければ上の名前つき HttpClient で REST のまま）。
// 戻すのは構成を外すだけでよい（コードは変えない）。
builder.Services.AddAuthzScopeGrpcClient(builder.Configuration);

// FR-04: RAG オーケストレーター
// FR-05, ADR-0034 (#970): 受信 Authorization を RetrievalService へ伝播するため要求文脈へ触る。
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
