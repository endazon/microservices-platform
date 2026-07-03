using Anthropic.SDK;
using LlmGateway.Api.Providers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LlmGateway.Api.Tests;

public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Llm:ApiKey"] = "test-key",
                ["Llm:Model"] = "claude-sonnet-4-6",
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test"
            }));
        builder.ConfigureServices(services =>
        {
            // API キーなしで動くようにスタブ LLM プロバイダーへ差し替え。
            // FR-11: ルーターはキー付きプロバイダ（claude/selfhosted）を解決するため、キー付きでも差し替える。
            services.RemoveAll<AnthropicClient>();
            services.RemoveAll<ILlmProvider>();
            services.AddSingleton<ILlmProvider, StubLlmProvider>();               // /embed 既定
            services.AddKeyedSingleton<ILlmProvider, StubLlmProvider>("claude");   // ティアB
            services.AddKeyedSingleton<ILlmProvider, StubLlmProvider>("selfhosted"); // ティアA
        });
    }
}

// テスト用スタブ LLM プロバイダー
file class StubLlmProvider : ILlmProvider
{
    public Task<CompletionResult> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
        => Task.FromResult(new CompletionResult("テスト回答", 10, 20));

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => Task.FromResult(new float[1536]);
}
