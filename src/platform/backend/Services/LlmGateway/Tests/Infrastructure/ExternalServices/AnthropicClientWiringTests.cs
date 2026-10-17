using Anthropic;
using AwesomeAssertions;
using LlmGateway.Infrastructure.ExternalServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LlmGateway.Tests.Infrastructure.ExternalServices;

// T-34, FR-11, ADR-0010, IADR-0528 (#1872), IADR-0562 (#1749): Program.cs の配線を固定する。
// 生成点（AnthropicClientFactory）の単体試験（T-33 / T-43）だけでは、Program.cs が生成点を通らずに
// 素の `new AnthropicClient()`（SDK の既定＝再試行 2 回・期限 10 分・環境変数の送信先）を登録しても気付けない。
// ここでは実ホストから AnthropicClient を解決し、SDK の既定を変えた値が載っていることを見る。
// ［2026-10-17 / #1749］旧 SDK の時代はハンドラ鎖（要求整形 → サニタイズ → 応答圧縮）を見ていた。鎖は撤去した。
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
                    [AnthropicClientFactory.ConfigKey] = timeoutSeconds,
                }));
        }
    }

    // T-34: ホストが解決する AnthropicClient の期限（SDK の Timeout）に、設定 `Llm:AnthropicTimeoutSeconds` の値が載る。
    [Fact]
    public void HostResolvedAnthropicClient_UsesConfiguredTimeout()
    {
        using var host = new RealAnthropicClientHost("7");

        var client = host.Services.GetRequiredService<AnthropicClient>();

        client.Timeout.Should().Be(TimeSpan.FromSeconds(7));
    }

    // T-34: 未設定ならホストでも 100 秒（既定の挙動不変。SDK 既定の null＝max_tokens からの算出にしない）。
    [Fact]
    public void HostResolvedAnthropicClient_WhenUnset_UsesHundredSeconds()
    {
        using var host = new RealAnthropicClientHost(null);

        var client = host.Services.GetRequiredService<AnthropicClient>();

        client.Timeout.Should().Be(TimeSpan.FromSeconds(100));
    }

    // T-34: 再試行 0・送信先は本番 URL の明示・API キーは設定値・HttpClient 側の期限は無限（期限の正は SDK の Timeout 1 か所）。
    [Fact]
    public void HostResolvedAnthropicClient_DisablesRetriesAndPinsDestination()
    {
        using var host = new RealAnthropicClientHost(null);

        var client = host.Services.GetRequiredService<AnthropicClient>();

        client.MaxRetries.Should().Be(0);
        client.BaseUrl.Should().Be("https://api.anthropic.com");
        client.ApiKey.Should().Be("test-key");
        client.AuthToken.Should().BeNull();
        client.HttpClient.Timeout.Should().Be(Timeout.InfiniteTimeSpan);
    }
}
