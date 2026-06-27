using KnowledgePlatform.Shared.Contracts.Dtos;
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

    public AiAnswerDto StubAnswer { get; set; } = new(
        "集約された回答 [1]",
        [new CitationDto(1, Guid.NewGuid(), "文書A", Guid.NewGuid(),
            "s3://bucket/a.md", 0.92f, "抜粋")],
        "claude-sonnet-4-6", 12, 34);

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
                ["Services:AiAnalysisService"] = "http://localhost:5004"
            }));

        builder.ConfigureServices(services =>
        {
            // 名前付きクライアント "AiAnalysisService" の通信をスタブハンドラに差し替える
            services.AddHttpClient("AiAnalysisService")
                .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(this));
        });
    }

    private sealed class StubHandler(BffTestFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            owner.LastForwardedAuthorization = request.Headers.Authorization?.ToString();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(owner.StubAnswer)
            };
            return Task.FromResult(response);
        }
    }
}
