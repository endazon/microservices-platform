using AiAnalysisService.Api.Services;
using KnowledgePlatform.Shared.Contracts.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiAnalysisService.Api.Tests;

public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test",
                ["Services:AuthorizationService"] = "http://localhost:5005",
                ["Services:RetrievalService"] = "http://localhost:5003",
                ["Services:LlmGateway"] = "http://localhost:5007"
            }));
        builder.ConfigureServices(services =>
        {
            // RAG オーケストレーターをスタブへ差し替え
            services.RemoveAll<IRagOrchestrator>();
            services.AddSingleton<IRagOrchestrator, StubRagOrchestrator>();
        });
    }
}

// テスト用スタブ RAG オーケストレーター
file class StubRagOrchestrator : IRagOrchestrator
{
    public Task<AiAnswerDto> AskAsync(string question, string userId,
        Dictionary<string, string> userAttributes, CancellationToken ct = default)
        => Task.FromResult(new AiAnswerDto("テスト回答", [], "claude-sonnet-4-6", 10, 20));
}
