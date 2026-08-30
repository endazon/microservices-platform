using Anthropic.SDK;
using LlmGateway.Domain.Ports;
using LlmGateway.Domain.Routing;
using LlmGateway.Infrastructure.ExternalServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LlmGateway.Tests;

public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Llm:ApiKey"] = "test-key",
                ["Llm:Model"] = "claude-opus-5",
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test"
            }));
        builder.ConfigureServices(services =>
        {
            // API キーなしで動くようにスタブ LLM プロバイダーへ差し替え。
            // FR-11: ルーターはキー付きプロバイダ（claude/selfhosted）を解決するため、キー付きでも差し替える。
            services.RemoveAll<AnthropicClient>();
            services.RemoveAll<ILlmProvider>();
            services.AddKeyedSingleton<ILlmProvider, StubLlmProvider>("claude");   // ティアB
            services.AddKeyedSingleton<ILlmProvider, StubLlmProvider>("selfhosted"); // ティアA
            services.AddKeyedSingleton<ILlmProvider, StubLlmProvider>("copilot");  // 最難関別経路（既定は無効エンドポイント）

            // FR-02: 埋め込みも API 基盤なしで動くようスタブへ差し替える。要求次元どおりのベクトルを返す。
            services.RemoveAll<IEmbeddingProvider>();
            services.AddKeyedSingleton<IEmbeddingProvider, StubEmbeddingProvider>("voyage");
            services.AddKeyedSingleton<IEmbeddingProvider, StubEmbeddingProvider>("selfhosted-embedding");
            // #992, [[IADR-0313]]: 決定的ローカル埋め込みは**外部依存が無い**（プロセス内計算）ので
            // スタブへ差し替えない。差し替えると「本物が動くこと」をここでは一切確かめられなくなる。
            services.AddKeyedSingleton<IEmbeddingProvider, DeterministicEmbeddingProvider>("deterministic-embedding");
        });
    }
}

// テスト用スタブ LLM プロバイダー
// IADR-0101: 受け取った MaxTokens を本文へ反映し、既定値がプロバイダまで到達することを
// テストから検証できるようにする（既存アサーションは "テスト回答" の部分一致のため影響しない）。
file class StubLlmProvider : ILlmProvider
{
    public Task<CompletionResult> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
        => Task.FromResult(new CompletionResult($"テスト回答 maxTokens={req.MaxTokens}", 10, 20));
}

// テスト用スタブ埋め込みプロバイダー（要求次元どおりのゼロベクトルを返す）。
file class StubEmbeddingProvider : IEmbeddingProvider
{
    public Task<float[]> EmbedAsync(string text, string model, int dimensions, EmbeddingRoutePurpose purpose, CancellationToken ct = default)
        => Task.FromResult(new float[dimensions]);
}
