using System.Runtime.CompilerServices;
using AiAnalysisService.Api.Foundation.Services;
using Platform.Shared.Contracts.Dtos;
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

// テスト用スタブ RAG オーケストレーター（番号付き出典を含む回答を返す）
file class StubRagOrchestrator : IRagOrchestrator
{
    public Task<AiAnswerDto> AskAsync(string question, string userId,
        Dictionary<string, string> userAttributes, CancellationToken ct = default)
        => Task.FromResult(Answer("テスト回答 [1]"));

    public Task<AiAnswerDto> AnalyzeAsync(AnalysisTaskRequest request, string userId,
        Dictionary<string, string> userAttributes, CancellationToken ct = default)
        => Task.FromResult(Answer($"分析結果({request.TaskType}) [1]"));

    // IADR-0037: ストリーミングのスタブ（citations → token* → done）。
    public async IAsyncEnumerable<AskEvent> AskStreamAsync(string question, string userId,
        Dictionary<string, string> userAttributes, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new AskCitationsEvent(
            [new CitationDto(1, Guid.NewGuid(), "文書A", Guid.NewGuid(), "s3://bucket/a.md", 0.9f, "抜粋")]);
        yield return new AskTokenEvent("テスト");
        yield return new AskTokenEvent("回答 [1]");
        await Task.Yield();
        yield return new AskDoneEvent(Guid.NewGuid(), "claude-sonnet-4-6", 10, 20);
    }

    private static AiAnswerDto Answer(string text)
        => new(
            text,
            [new CitationDto(1, Guid.NewGuid(), "文書A", Guid.NewGuid(),
                "s3://bucket/a.md", 0.9f, "抜粋")],
            "claude-sonnet-4-6", 10, 20);
}
