using AiAnalysisService.Api.Foundation.Services;
using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Dtos;
using Microsoft.Extensions.Configuration;

namespace AiAnalysisService.Api.Tests;

// FR-05: ABAC スコープ解決の通信失敗時に deny-by-default へ縮退することを検証する。
public class RagOrchestratorScopeTests
{
    // 認可サービスへの通信が例外（ネットワーク障害・タイムアウト）で失敗しても、
    // 500 を伝播させず空回答（deny-by-default）へ縮退する。
    [Fact]
    public async Task AskAsync_AuthzHttpFailure_DegradesToEmptyAnswer()
    {
        var orchestrator = new RagOrchestrator(
            new ThrowingHttpClientFactory(new HttpRequestException("connection refused")),
            BuildConfig());

        AiAnswerDto? answer = null;
        var act = async () => answer = await orchestrator.AskAsync(
            "質問", "user-1", new Dictionary<string, string>());

        await act.Should().NotThrowAsync();
        answer!.Citations.Should().BeEmpty();
    }

    // タイムアウト（HttpClient は TaskCanceledException を投げる）でも同様に縮退する。
    [Fact]
    public async Task AskAsync_AuthzTimeout_DegradesToEmptyAnswer()
    {
        var orchestrator = new RagOrchestrator(
            new ThrowingHttpClientFactory(new TaskCanceledException("timeout")),
            BuildConfig());

        AiAnswerDto? answer = null;
        var act = async () => answer = await orchestrator.AskAsync(
            "質問", "user-1", new Dictionary<string, string>());

        await act.Should().NotThrowAsync();
        answer!.Citations.Should().BeEmpty();
    }

    private static IConfiguration BuildConfig()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Llm:DefaultModel"] = "claude-sonnet-4-6"
            })
            .Build();

    // 生成する HttpClient がすべて指定の例外を投げるスタブファクトリ。
    private sealed class ThrowingHttpClientFactory(Exception toThrow) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new ThrowingHandler(toThrow)) { BaseAddress = new Uri("http://localhost") };
    }

    private sealed class ThrowingHandler(Exception toThrow) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw toThrow;
    }
}
