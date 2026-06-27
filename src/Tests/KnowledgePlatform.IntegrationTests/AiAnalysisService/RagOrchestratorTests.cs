using AiAnalysisService.Api.Services;
using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Http.Json;

namespace KnowledgePlatform.IntegrationTests.AiAnalysisService;

// FR-04, FR-07, UC-01, UC-02: AiAnalysisService RAG フロー統合テスト
// DownstreamサービスはスタブHTTPハンドラで代替（外部依存なし）
[Trait("Category", "Integration")]
public sealed class RagOrchestratorTests : IClassFixture<RagIntegrationFactory>
{
    private readonly HttpClient _client;

    public RagOrchestratorTests(RagIntegrationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PostAsk_ReturnsOkWithAnswer()
    {
        // Act
        var resp = await _client.PostAsJsonAsync("/analysis/ask",
            new { Question = "プロジェクト憲章の承認フローを教えて" });

        // Assert
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var answer = await resp.Content.ReadFromJsonAsync<AiAnswerDto>();
        answer.Should().NotBeNull();
        answer!.Answer.Should().NotBeNullOrEmpty();
    }

    // FR-07, UC-02: 指定データ範囲での分析・比較・抽出
    [Fact]
    public async Task PostAnalyze_ReturnsOkWithAnswer()
    {
        var resp = await _client.PostAsJsonAsync("/analysis/analyze", new
        {
            instruction = "2025 年度の規程変更を比較して",
            taskType = "Compare",
            range = new { attributeFilters = new { year = new[] { "2025" } } }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var answer = await resp.Content.ReadFromJsonAsync<AiAnswerDto>();
        answer.Should().NotBeNull();
        answer!.Answer.Should().NotBeNullOrEmpty();
    }
}

// スタブ RagOrchestrator を使う WebApplicationFactory
public sealed class RagIntegrationFactory : WebApplicationFactory<global::AiAnalysisService.Api.AiAnalysisServiceTestMarker>
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
            services.RemoveAll<IRagOrchestrator>();
            services.AddSingleton<IRagOrchestrator, StubRagOrchestrator>();
        });
    }
}

file class StubRagOrchestrator : IRagOrchestrator
{
    public Task<AiAnswerDto> AskAsync(string question, string userId,
        Dictionary<string, string> userAttributes, CancellationToken ct = default)
        => Task.FromResult(Answer($"「{question}」への回答（統合テストスタブ）"));

    public Task<AiAnswerDto> AnalyzeAsync(AnalysisTaskRequest request, string userId,
        Dictionary<string, string> userAttributes, CancellationToken ct = default)
        => Task.FromResult(Answer($"「{request.Instruction}」の{request.TaskType}結果（統合テストスタブ）"));

    private static AiAnswerDto Answer(string text)
        => new(
            Answer: text,
            Citations: [],
            Model: "claude-sonnet-4-6",
            InputTokens: 50,
            OutputTokens: 100);
}
