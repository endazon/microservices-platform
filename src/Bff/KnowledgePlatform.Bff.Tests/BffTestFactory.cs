using KnowledgePlatform.Shared.Contracts.Dtos;
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
                ["Services:AiAnalysisService"] = "http://localhost:5004",
                ["Services:FeedbackService"] = "http://localhost:5008",
                ["Services:DashboardService"] = "http://localhost:5009"
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

            // FR-10: /bff/dashboard/summary は AdminOnly。テストでは Keycloak/JWT に依存せず
            // TestAuthHandler で認証し、既定で管理者ロールを付与する（既定スキームを Test に切替）。
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    private sealed class StubHandler(BffTestFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            owner.LastForwardedAuthorization = request.Headers.Authorization?.ToString();
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
                response = new HttpResponseMessage(HttpStatusCode.OK)
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
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(usage)
            };
            return Task.FromResult(response);
        }
    }
}
