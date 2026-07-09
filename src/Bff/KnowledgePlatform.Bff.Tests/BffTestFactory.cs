using KnowledgePlatform.Shared.Contracts.Dtos;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Audit;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Introspection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace KnowledgePlatform.Bff.Tests;

public class BffTestFactory : WebApplicationFactory<Program>
{
    // FR-15 BFF テスト: 構成情報 API の集約対象の自己申告をスタブ化し、監査記録を捕捉する。
    public EffectiveCollection StubEffective { get; set; } = EffectiveCollection.Empty;
    public List<(string Action, string Subject, string Outcome)> RecordedAudits { get; } = [];

    // FR-15 (#145): 即時ドリフト検出のアラート発火（IDriftAlertSink）を捕捉する。
    public List<DriftReportDto> AlertedReports { get; } = [];

    // FR-04 BFF テスト: 後段 AiAnalysisService への転送を捕捉・スタブ化する
    public string? LastForwardedAuthorization { get; private set; }

    // FR-07 BFF テスト: 後段が返すステータスコードを差し替え、非 2xx の透過を検証する。
    public HttpStatusCode StubStatusCode { get; set; } = HttpStatusCode.OK;

    public AiAnswerDto StubAnswer { get; set; } = new(
        "集約された回答 [1]",
        [new CitationDto(1, Guid.NewGuid(), "文書A", Guid.NewGuid(),
            "s3://bucket/a.md", 0.92f, "抜粋")],
        "claude-sonnet-4-6", 12, 34);

    // FR-08 BFF テスト: 後段 FeedbackService への転送を捕捉・スタブ化する。
    public string? LastFeedbackForwardedAuthorization { get; private set; }

    // FR-10 BFF テスト: 後段 DashboardService への転送を捕捉・スタブ化する。
    public string? LastDashboardForwardedAuthorization { get; private set; }

    // FR-10 BFF テスト: 後段が返すステータスの差し替え・null 応答の再現（非 2xx 透過・502 分岐の検証用）。
    public HttpStatusCode DashboardStubStatusCode { get; set; } = HttpStatusCode.OK;
    public HttpStatusCode FeedbackStatsStubStatusCode { get; set; } = HttpStatusCode.OK;
    public bool DashboardReturnsNullBody { get; set; }

    // FR-03/FR-05 BFF テスト（SC-01 横断検索）: ABAC スコープ解決の許可可否と検索結果をスタブ制御する。
    public bool SearchScopeGranted { get; set; } = true;
    // FR-05 (SC-03): スコープ解決が返す許可フィルタ。既定は空（＝条件なしで全件許可）。SC-03 の
    // 属性不一致 → 404 秘匿を検証する際に非空へ差し替える。
    public List<AttributeFilter> ScopeFilters { get; set; } = [];
    public SearchResponse StubSearchResponse { get; set; } = new(
        [new SearchResultDto(Guid.NewGuid(), Guid.NewGuid(), "経費規程 2025",
            "第3条 …", 0.91f, "s3://bucket/expense.md",
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, ["hr"])],
        1, 5);

    // FR-06 BFF テスト（SC-03 文書閲覧）: DocumentService の応答をスタブ制御する。
    public static readonly Guid StubDocumentId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public HttpStatusCode DocumentStatusCode { get; set; } = HttpStatusCode.OK;
    // FR-06 BFF テスト（SC-05 文書管理・書き込み）: 書き込み（POST/PUT/PATCH/DELETE）応答のステータス。
    // GET（スコープ確認）とは独立に差し替えられる（検証 400・楽観ロック競合 409 の透過検証用）。
    public HttpStatusCode DocumentWriteStatusCode { get; set; } = HttpStatusCode.OK;
    public DocumentDto StubDocument { get; set; } = new()
    {
        Id = StubDocumentId,
        Title = "経費規程 2025",
        Status = "published",
        MarkdownUri = "storage://bucket/expense.md",
        Version = 3,
        Attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" },
        Tags = ["hr"],
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
    public List<DocumentDto> StubDocumentList { get; set; } =
    [
        new()
        {
            Id = StubDocumentId, Title = "経費規程 2025", Status = "published",
            Version = 3, Attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" },
            Tags = ["hr"],
        },
        new()
        {
            Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), Title = "取締役会議事録",
            Status = "published", Version = 1,
            Attributes = new Dictionary<string, string> { ["confidentiality"] = "secret" }, Tags = ["board"],
        },
    ];
    public List<DocumentVersionDto> StubVersions { get; set; } =
    [
        new() { DocumentId = StubDocumentId, Version = 3, Title = "経費規程 2025", Status = "published", ChangeNote = "第3条改定", CreatedAt = DateTimeOffset.UtcNow },
        new() { DocumentId = StubDocumentId, Version = 2, Title = "経費規程 2025", Status = "published", CreatedAt = DateTimeOffset.UtcNow.AddDays(-30) },
    ];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Redis:ConnectionString"] = "localhost:6379",
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test",
                ["Services:RetrievalService"] = "http://localhost:5003",
                ["Services:DocumentService"] = "http://localhost:5001",
                ["Services:AiAnalysisService"] = "http://localhost:5004",
                ["Services:FeedbackService"] = "http://localhost:5008",
                ["Services:DashboardService"] = "http://localhost:5009",
                // FR-15: 構成情報 API テスト。定期ドリフト検出は無効化し、構成バージョンを固定する。
                ["Drift:Enabled"] = "false",
                ["Config:GitCommit"] = "abc1234",
                ["Config:AppliedAt"] = "2026-07-07T00:00:00Z",
                ["Config:AppliedBy"] = "argocd"
            }));

        builder.ConfigureServices(services =>
        {
            // 名前付きクライアント "AiAnalysisService" の通信をスタブハンドラに差し替える
            services.AddHttpClient("AiAnalysisService")
                .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(this));
            // FR-08: 名前付きクライアント "FeedbackService" の通信をスタブハンドラに差し替える
            services.AddHttpClient("FeedbackService")
                .ConfigurePrimaryHttpMessageHandler(() => new FeedbackStubHandler(this));
            // FR-10: 名前付きクライアント "DashboardService" の通信をスタブハンドラに差し替える
            services.AddHttpClient("DashboardService")
                .ConfigurePrimaryHttpMessageHandler(() => new DashboardStubHandler(this));
            // FR-03/FR-05 (SC-01 横断検索): AuthorizationService / RetrievalService をスタブ化する。
            services.AddHttpClient("AuthorizationService")
                .ConfigurePrimaryHttpMessageHandler(() => new AuthzStubHandler(this));
            services.AddHttpClient("RetrievalService")
                .ConfigurePrimaryHttpMessageHandler(() => new RetrievalStubHandler(this));
            // FR-06 (SC-03 文書閲覧): DocumentService をスタブ化する。
            services.AddHttpClient("DocumentService")
                .ConfigurePrimaryHttpMessageHandler(() => new DocumentStubHandler(this));

            // FR-10: /bff/dashboard/summary は AdminOnly。テストでは Keycloak/JWT に依存せず
            // TestAuthHandler で認証し、既定で管理者ロールを付与する（既定スキームを Test に切替）。
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // FR-15: 自己申告の収集と監査ログをスタブ化する（最後の登録が解決される）。
            services.AddSingleton<IEffectiveConfigCollector>(new StubEffectiveConfigCollector(this));
            services.AddSingleton<IAuditLogger>(new RecordingAuditLogger(this));
            // FR-15 (#145): アラート発火を捕捉するシンクに差し替える（既定の LoggingDriftAlertSink を上書き）。
            services.AddSingleton<IDriftAlertSink>(new RecordingDriftAlertSink(this));
        });
    }

    // FR-15 (#145): ドリフト警告（IDriftAlertSink.AlertAsync）の呼び出しを捕捉する。
    private sealed class RecordingDriftAlertSink(BffTestFactory owner) : IDriftAlertSink
    {
        public Task AlertAsync(DriftReportDto report, CancellationToken ct = default)
        {
            owner.AlertedReports.Add(report);
            return Task.CompletedTask;
        }
    }

    // FR-15: 自己申告の収集をテスト制御の EffectiveCollection に差し替える。
    private sealed class StubEffectiveConfigCollector(BffTestFactory owner) : IEffectiveConfigCollector
    {
        public Task<EffectiveCollection> CollectAsync(CancellationToken ct = default) =>
            Task.FromResult(owner.StubEffective);
    }

    // FR-15: 監査記録を捕捉して検証可能にする。
    private sealed class RecordingAuditLogger(BffTestFactory owner) : IAuditLogger
    {
        public void Record(string action, string subject, string outcome, string? detail = null) =>
            owner.RecordedAudits.Add((action, subject, outcome));
    }

    private sealed class StubHandler(BffTestFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            owner.LastForwardedAuthorization = request.Headers.Authorization?.ToString();
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            // IADR-0037: /analysis/ask/stream は SSE を返す（BFF は逐次中継する）。
            if (path.EndsWith("/ask/stream", StringComparison.Ordinal))
            {
                const string sse =
                    "event: citations\ndata: {\"citations\":[{\"number\":1,\"documentTitle\":\"文書A\"}]}\n\n" +
                    "event: token\ndata: {\"text\":\"回答\"}\n\n" +
                    "event: done\ndata: {\"answerId\":\"11111111-1111-1111-1111-111111111111\",\"model\":\"m\",\"inputTokens\":1,\"outputTokens\":2}\n\n";
                var sseResp = new HttpResponseMessage(owner.StubStatusCode)
                {
                    Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream")
                };
                return Task.FromResult(sseResp);
            }

            var response = new HttpResponseMessage(owner.StubStatusCode)
            {
                Content = JsonContent.Create(owner.StubAnswer)
            };
            return Task.FromResult(response);
        }
    }

    // FR-08: FeedbackService への転送をスタブ化する。POST は 201+FeedbackDto、stats は FeedbackStatsDto を返す。
    private sealed class FeedbackStubHandler(BffTestFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            owner.LastFeedbackForwardedAuthorization = request.Headers.Authorization?.ToString();
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            HttpResponseMessage response;
            if (path.Contains("/stats"))
            {
                response = new HttpResponseMessage(owner.FeedbackStatsStubStatusCode)
                {
                    Content = JsonContent.Create(new FeedbackStatsDto(3, 1, 4, 0.75))
                };
            }
            else
            {
                response = new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new FeedbackDto(
                        Guid.NewGuid(), Guid.NewGuid(), "up", null, null,
                        "anonymous", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))
                };
            }
            return Task.FromResult(response);
        }
    }

    // FR-10: DashboardService への転送をスタブ化する。/dashboard/summary は DashboardUsageDto を返す。
    private sealed class DashboardStubHandler(BffTestFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            owner.LastDashboardForwardedAuthorization = request.Headers.Authorization?.ToString();
            var usage = new DashboardUsageDto(
                5, 3,
                [new UsagePointDto(new DateOnly(2026, 7, 3), "search", 5),
                 new UsagePointDto(new DateOnly(2026, 7, 3), "answer", 3)],
                [new SearchTrendDto("経費", 4), new SearchTrendDto("有給", 1)]);
            // 502 分岐の検証: 2xx でも本文が null（JSON リテラル "null"）なら BFF は 502 を返す。
            var content = owner.DashboardReturnsNullBody
                ? new StringContent("null", System.Text.Encoding.UTF8, "application/json")
                : (HttpContent)JsonContent.Create(usage);
            var response = new HttpResponseMessage(owner.DashboardStubStatusCode)
            {
                Content = content
            };
            return Task.FromResult(response);
        }
    }

    // FR-05 (SC-01/SC-03): AuthorizationService /authz/scope をスタブ化する。Granted は SearchScopeGranted、
    // 許可フィルタは ScopeFilters（既定は空＝全件許可）で制御する。
    private sealed class AuthzStubHandler(BffTestFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var scope = new AccessScopeResponse("tester", owner.ScopeFilters, owner.SearchScopeGranted);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(scope)
            });
        }
    }

    // FR-06 (SC-03): DocumentService をスタブ化する。/documents（一覧）・/documents/{id}（詳細・状態可変）・
    // /documents/{id}/versions（版履歴）をパスで振り分ける。
    private sealed class DocumentStubHandler(BffTestFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var method = request.Method;

            if (path.EndsWith("/versions", StringComparison.Ordinal))
                return Ok(owner.StubVersions);

            if (path == "/documents")
            {
                // FR-06 (SC-05): 新規作成。検証エラーは DocumentWriteStatusCode で再現し透過を確認する。
                if (method == HttpMethod.Post)
                    return owner.DocumentWriteStatusCode != HttpStatusCode.OK
                        ? Status(owner.DocumentWriteStatusCode, new { errors = new { title = new[] { "タイトルは必須です。" } } })
                        : Json(HttpStatusCode.Created, owner.StubDocument);
                return Ok(owner.StubDocumentList);
            }

            // GET /documents/{id}（詳細・スコープ確認にも使われる）
            if (method == HttpMethod.Get)
            {
                if (owner.DocumentStatusCode != HttpStatusCode.OK)
                    return Task.FromResult(new HttpResponseMessage(owner.DocumentStatusCode));
                return Ok(owner.StubDocument);
            }

            // FR-06 (SC-05): 書き込み（PUT/PATCH/POST publish・archive/DELETE）。
            if (owner.DocumentWriteStatusCode != HttpStatusCode.OK)
                return Status(owner.DocumentWriteStatusCode,
                    new { error = "version_conflict", expectedVersion = 1, currentVersion = 3 });
            if (method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            return Ok(owner.StubDocument);
        }

        private static Task<HttpResponseMessage> Ok<T>(T body) => Json(HttpStatusCode.OK, body);

        private static Task<HttpResponseMessage> Json<T>(HttpStatusCode code, T body) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = JsonContent.Create(body) });

        private static Task<HttpResponseMessage> Status<T>(HttpStatusCode code, T body) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = JsonContent.Create(body) });
    }

    // FR-03 (SC-01): RetrievalService /search をスタブ化する。StubSearchResponse を返す。
    private sealed class RetrievalStubHandler(BffTestFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(owner.StubSearchResponse)
            });
        }
    }
}
