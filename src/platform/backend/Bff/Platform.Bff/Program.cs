using Knowledge.Bff.Endpoints.Usage;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Observability;
using Platform.Bff.Composition;
using Platform.Bff.Foundation.Session;

const string ServiceName = "microservices-platform.bff";

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddPlatformLogging(builder.Configuration, ServiceName);

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddPlatformAuth(builder.Configuration);
builder.Services.AddPlatformHealthChecks()
    .AddRedis(
        builder.Configuration["Redis:ConnectionString"] ?? "redis:6379",
        tags: ["ready"])
    .AddUrlGroup(
        new Uri((builder.Configuration["Services:RetrievalService"] ?? "http://retrieval-service:5003") + "/health/live"),
        "retrieval-service", tags: ["ready"])
    .AddUrlGroup(
        new Uri((builder.Configuration["Services:AiAnalysisService"] ?? "http://aianalysis-service:5004") + "/health/live"),
        "aianalysis-service", tags: ["ready"])
    .AddUrlGroup(
        new Uri((builder.Configuration["Services:FeedbackService"] ?? "http://feedback-service:5008") + "/health/live"),
        "feedback-service", tags: ["ready"])
    .AddUrlGroup(
        new Uri((builder.Configuration["Services:DashboardService"] ?? "http://dashboard-service:5009") + "/health/live"),
        "dashboard-service", tags: ["ready"]);
builder.Services.AddOpenApi();
builder.Services.AddHttpClient();

// FR-15, ADR-0018: 構成情報 API（実効構成の集約・ドリフト定期検出・監査）を BFF へ同居させる。
builder.AddPlatformConfigInspection();

// FR-04, UC-01: AiAnalysisService 集約用の名前付き HTTP クライアント
builder.Services.AddHttpClient("AiAnalysisService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:AiAnalysisService"]
        ?? "http://aianalysis-service:5004"));

// FR-08, UC-01: FeedbackService 集約用の名前付き HTTP クライアント
builder.Services.AddHttpClient("FeedbackService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:FeedbackService"]
        ?? "http://feedback-service:5008"));

// FR-10, UC-05: DashboardService 集約用の名前付き HTTP クライアント
builder.Services.AddHttpClient("DashboardService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:DashboardService"]
        ?? "http://dashboard-service:5009"));

// FR-10, SC-10, ADR-0006, IADR-0343 (#1103): 利用状況イベント（POST /dashboard/events）の発火側。
// 受け口・集計・画面は在ったが**投入する製品コードが 1 本も無く**、SC-10 の利用状況・検索傾向は
// 恒久的に 0 だった。送出は上の名前付きクライアントを使い、**要求の応答経路には載せない**
// （有界の列 ＋ 常駐ドレイン。検索 p95 に計測の往復を足さない）。
builder.Services.AddKnowledgeUsageEventReporting();

// NFR-02, ADR-0044, ADR-0071, ADR-0072, ADR-0076 決定 4, [[IADR-0378]] (#1203):
// **合成監視の標識**。BFF は外部から到達し得る面なので、判定は**検証済み JWT の主体だけ**で行う
// （受信ヘッダは見ない）。許可集合が空なら合成は 1 件も存在しない（fail-closed）。
builder.Services.AddSyntheticMonitoring(builder.Configuration);
builder.Services.AddOpenTelemetry()
    // 🔴 宣言が無い Meter は収集されない＝**送出の失敗が静かに消える**。
    // Meter 名は BFF のサービス名と同じなので収集対象そのものは増えない。
    .WithMetrics(metrics => metrics.AddMeter(UsageEventMetrics.MeterName));

// FR-03, UC-01, SC-01: 横断検索の集約用。ABAC スコープ解決（AuthorizationService）→ 検索（RetrievalService）。
builder.Services.AddHttpClient("AuthorizationService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:AuthorizationService"]
        ?? "http://authorization-service:5005"));
// NFR-09, ADR-0029, ADR-0075, IADR-0379 (#1201): 同じ解決を gRPC でも呼べるようにする（参照実装・opt-in）。
// `Services:AuthorizationServiceGrpc`（h2c アドレス）が在るときだけ登録され、BffScopeResolver がこちらを使う。
// 資格情報は BFF 自身の s2s トークン（`ServiceToken:*`。利用者の JWT ではない）。並走中の正は REST。
builder.Services.AddAuthzScopeGrpcClient(builder.Configuration);
builder.Services.AddHttpClient("RetrievalService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:RetrievalService"]
        ?? "http://retrieval-service:5003"));

// FR-17, UC-10, #916a: グラフ読み取り（GraphService）。**利用者の JWT を伝播して呼ぶ**
// —— GraphService は自分で ABAC を解決する型であり、本文で scope を渡す方式は採らない。
//
// 既定は :8080（メッシュ内の実 Service ポート）。IADR-0089 の「コード既定＝ローカル開発ポート、
// manifest が :8080 へ上書き」は**先発サービスの経緯**であり、**後発（conversion / AST 系）は
// コード既定を 8080 にする**——GraphService も後発なのでそちらに揃える。上書き漏れで不達になる
// 面（#342 の 21 秒タイムアウト）を最初から作らない。check-bff-downstreams.js が実効ポートを検査する。
builder.Services.AddHttpClient("GraphService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:GraphService"]
        ?? "http://graph-service:8080"));

// FR-16, UC-09, SC-12, ADR-0024, #452: MCP クライアント登録管理（McpServer の /mcp-clients*）。
// **利用者の JWT を伝播して呼ぶ** —— 後段も AdminOnly を強制する二重ゲートである。
//
// 既定は :8080（メッシュ内の実 Service ポート）。IADR-0089 の「コード既定＝ローカル開発ポート、
// manifest が :8080 へ上書き」は先発サービスの経緯であり、**後発はコード既定を 8080 にする**
// （GraphService と同じ扱い）。上書き漏れで不達になる面（#342 の 21 秒タイムアウト）を最初から作らない。
//
// 🔴 **ホスト名は `mcp-service` である**（chart のキーは `mcp`）。helm の deployment.yaml /
// service.yaml が `{{ $name }}-service` を組むため、キーを `mcp-server` にすると
// Service 名は `mcp-server-service` になる。compose のサービス名も `mcp-service` へ揃えてある。
builder.Services.AddHttpClient("McpServer", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:McpServer"]
        ?? "http://mcp-service:8080"));

// FR-22, UC-11, ADR-0037, IADR-0215 / IADR-0346 (#600): 利用者本人へのアプリ内通知の集約用。
// 🔴 **コード既定を :8080 にする**（後発サービスの規約。#342 の上書き漏れで不達になる面を最初から作らない）。
// ホスト名 `notification-service` は送出側 DocumentService のコード既定・compose のサービス名・
// helm の `{{ $name }}-service` と文字列一致する（IADR-0288）。
// **readiness の UriHealthCheck には入れない** —— 通知は Should であり、後段の不調で BFF 全体を
// not-ready にするのは fail-safe の後退である（McpServer / DocumentService と同じ扱い）。
builder.Services.AddHttpClient("NotificationService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:NotificationService"]
        ?? "http://notification-service:8080"));

// FR-13, UC-07, SC-04, ADR-0011 / ADR-0073 決定 4, IADR-0020 / IADR-0335 / IADR-0355 (#1199):
// Wiki 前段（WikiService の /wiki/*）の集約用。**利用者の JWT を伝播して呼ぶ** ——
// WikiService は IWikiAccessResolver で自分で ABAC を解決する型であり、本文で scope を渡す方式は
// 採らない（GraphService と同じ判断）。
//
// 🔴 **コード既定を :8080 にする**（後発サービスの規約。IADR-0089 / #342 の上書き漏れで不達になる面を
// 最初から作らない）。ホスト名 `wiki-service` は compose のサービス名・helm の `{{ $name }}-service`
// （chart キー `wiki`）と文字列一致する。**helm の `Services__WikiService` は同値で既に在り**
// （named client 不在のまま先に入っていた宙ぶらりん項目が、ここで実体を得る）、
// **compose 側の上書きは不要である**（コード既定が既に :8080。check-bff-downstreams.js の不変条件を
// 上書き無しで満たす）。
//
// **readiness の UriHealthCheck には入れない** —— Wiki 閲覧は 1 機能であり、後段の不調で BFF 全体を
// not-ready にするのは fail-safe の後退である（McpServer / DocumentService / NotificationService と同じ扱い）。
builder.Services.AddHttpClient("WikiService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:WikiService"]
        ?? "http://wiki-service:8080"));

// FR-06, UC-03/UC-07, SC-03: 文書閲覧の集約用。ABAC スコープ解決（AuthorizationService）→ 文書取得。
builder.Services.AddHttpClient("DocumentService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:DocumentService"]
        ?? "http://document-service:5001"));

// FR-12, UC-06, SC-07: 変換ジョブ管理の集約用（管理者・運用者限定）。ワーカーの HTTP サーフェスは 8080。
builder.Services.AddHttpClient("ConversionService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:ConversionService"]
        ?? "http://conversion-service:8080"));

// FR-01, FR-02, UC-04, SC-06: データソース管理の集約用（管理者・運用者限定）。
builder.Services.AddHttpClient("DataSourceService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:DataSourceService"]
        ?? "http://datasource-service:5002"));

// Issue #283, AST/FR-17, AST/UC-06, IADR-0070: AST 設定画面（全体前提条件）の集約用。ConfigurationService(/assumptions)
// へ pass-through する。AST 未デプロイ時は不達（BFF が 502 へ縮退）で足を引かないよう、readiness の
// UriHealthCheck には含めない（可変ユニットの導入有無で BFF の可用性を左右させない・fail-safe）。
builder.Services.AddHttpClient("ConfigurationService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:ConfigurationService"]
        ?? "http://configuration-service:8080"));

// Issue #287, FR-14, IADR-0071: AST リスク設定（AST/SC-02）・統制状態参照（AST/SC-03）の集約用。RiskManagementService
// (/risk-controls/*) へ pass-through する。ConfigurationService と同じく、AST 未デプロイ時の不達で BFF の可用性を
// 左右させないよう readiness の UriHealthCheck には含めない（fail-safe）。
builder.Services.AddHttpClient("RiskManagementService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:RiskManagementService"]
        ?? "http://risk-management-service:8080"));

// Issue #288, FR-14, IADR-0072: AST 監視銘柄（AST/SC-02 watchlist）の集約用。MarketMonitorService
// (/monitor/*) へ pass-through する。ConfigurationService/RiskManagementService と同じく、AST 未デプロイ時の
// 不達で BFF の可用性を左右させないよう readiness の UriHealthCheck には含めない（fail-safe）。
builder.Services.AddHttpClient("MarketMonitorService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:MarketMonitorService"]
        ?? "http://market-monitor-service:8080"));

// FR-06, ADR-0014/ADR-0015: 正規化 Markdown 本文の読み取り用オブジェクトストレージ（storage://）。
// 未構成時は NullObjectStorageClient（CanResolve=false）へ縮退し、本文はプレースホルダへフォールバックする。
builder.Services.AddPlatformObjectStorage(builder.Configuration);

// NFR, ADR-0032 / IADR-0251 / IADR-0273 / #439 第 3 段: BFF セッション（Token Handler）。
// **既定の認証スキームは振り分け（BffSmart）である**（3b①）—— `Authorization: Bearer` が
// 在れば JwtBearer、無ければセッション Cookie。SPA はセッション方式（3b②③で切り替え済み）。
builder.Services.AddBffSession(builder.Configuration);

var app = builder.Build();

app.UsePlatformMiddleware();

// ADR-0032 §決定 / IADR-0251 決定 1: CSRF 対策の 2 枚目の壁。
// **セッション Cookie を運ぶ状態変更リクエストにだけ**カスタムヘッダを要求する
// （Bearer 呼び出しは対象外 —— ブラウザが自動で付ける資格情報ではないため CSRF が成立しない）。
app.UseMiddleware<CsrfHeaderMiddleware>();
// NFR, ADR-0032 / IADR-0273 決定 4 / #439: セッション認証のリクエストに、チケット保存済みの
// アクセストークンを Authorization ヘッダとして昇格する（下流転送の契約を変えないための橋。
// CSRF 検査の**後**に置く —— 拒否されたリクエストにトークンを付けない）。
app.UseMiddleware<SessionTokenPropagationMiddleware>();
app.MapPlatformHealthChecks();
app.MapOpenApi();

// FR-14, IADR-0063: BFF エンドポイントは合成点（BffEndpointComposition）経由で一括登録する。
// ユニット追加時は合成点の登録簿へ 1 行追加するだけで組み込める（Program.cs は不変）。
app.MapComposedBffEndpoints();

app.Run();

public partial class Program { }
