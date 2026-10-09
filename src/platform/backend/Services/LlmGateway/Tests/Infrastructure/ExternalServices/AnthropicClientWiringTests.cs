using System.Net;
using System.Reflection;
using Anthropic.SDK;
using AwesomeAssertions;
using LlmGateway.Infrastructure.ExternalServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlmGateway.Tests.Infrastructure.ExternalServices;

// FR-11, ADR-0010, IADR-0114, IADR-0528 (#1872): Program.cs の配線を固定する。
// AnthropicHttpClient 単体の試験（T-33）だけでは、Program.cs が Create を呼ばずに素の HttpClient を渡しても、
// Create のハンドラ鎖から応答圧縮を外しても気付けない（PR #1873 の監査が変異で実測）。
// ここでは ① 実ホストから AnthropicClient を解決して期限が設定値になること、② Create のハンドラ鎖を直接見る。
[Trait("TestKind", "Unit")]
public class AnthropicClientWiringTests
{
    // TestWebApplicationFactory は AnthropicClient を取り除くため使わない（Program.cs の登録そのものを見る）。
    private sealed class RealAnthropicClientHost(string? timeoutSeconds) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Llm:ApiKey"] = "test-key",
                    ["Otlp:Endpoint"] = "http://localhost:4317",
                    ["Auth:Authority"] = TestServiceTokens.Issuer,
                    [AnthropicHttpClient.ConfigKey] = timeoutSeconds,
                }));
        }
    }

    // AnthropicClient は渡された HttpClient を非公開のプロパティ（HttpClient）に持つ。試験だけの読み取り。
    private static HttpClient HttpClientOf(AnthropicClient client) =>
        (HttpClient)typeof(AnthropicClient)
            .GetProperty("HttpClient", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(client)!;

    // HttpClient はハンドラを公開しないため、基底 HttpMessageInvoker の非公開フィールドを読む（試験だけの読み取り）。
    private static HttpMessageHandler HandlerOf(HttpClient client) =>
        (HttpMessageHandler)typeof(HttpMessageInvoker)
            .GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)!;

    // T-34: ホストが解決する AnthropicClient の HttpClient に、設定 `Llm:AnthropicTimeoutSeconds` の値が載る。
    [Fact]
    public void HostResolvedAnthropicClient_UsesConfiguredTimeout()
    {
        using var host = new RealAnthropicClientHost("7");

        var client = host.Services.GetRequiredService<AnthropicClient>();

        HttpClientOf(client).Timeout.Should().Be(TimeSpan.FromSeconds(7));
    }

    // T-34: 未設定ならホストでも 100 秒（既定の挙動不変）。
    [Fact]
    public void HostResolvedAnthropicClient_WhenUnset_UsesHundredSeconds()
    {
        using var host = new RealAnthropicClientHost(null);

        var client = host.Services.GetRequiredService<AnthropicClient>();

        HttpClientOf(client).Timeout.Should().Be(TimeSpan.FromSeconds(100));
    }

    // T-34: ホストが解決する AnthropicClient のハンドラ鎖が Create と同じ形である
    // （サニタイズの委譲ハンドラ → 応答圧縮を有効にした HttpClientHandler）。
    [Fact]
    public void HostResolvedAnthropicClient_HasSanitizingHandlerChain()
    {
        using var host = new RealAnthropicClientHost(null);

        var client = host.Services.GetRequiredService<AnthropicClient>();

        AssertSanitizingChain(HandlerOf(HttpClientOf(client)));
    }

    // T-34: Create のハンドラ鎖（IADR-0114）。一次ハンドラは AutomaticDecompression = All。
    [Fact]
    public void Create_BuildsSanitizingHandlerOverDecompressingHttpClientHandler()
    {
        using var client = AnthropicHttpClient.Create(
            new ConfigurationBuilder().Build(), NullLogger<AnthropicResponseSanitizingHandler>.Instance);

        AssertSanitizingChain(HandlerOf(client));
    }

    private static void AssertSanitizingChain(HttpMessageHandler handler)
    {
        var sanitizer = handler.Should().BeOfType<AnthropicResponseSanitizingHandler>().Subject;
        var inner = sanitizer.InnerHandler.Should().BeOfType<HttpClientHandler>().Subject;
        inner.AutomaticDecompression.Should().Be(DecompressionMethods.All);
    }
}
