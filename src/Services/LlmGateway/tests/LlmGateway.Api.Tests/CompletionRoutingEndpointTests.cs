using FluentAssertions;
using LlmGateway.Api.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace LlmGateway.Api.Tests;

// FR-11, ADR-0010: /complete が機密区分・用途に応じて呼び出し先を切り替え、
// 許容ティアが無い場合は送信を拒否（縮退）することを検証する。
public class CompletionRoutingEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private record CompletionResponse(
        string Text, string Model, int InputTokens, int OutputTokens,
        bool Sent, string? Endpoint, string? RoutingReason);

    // FR-11: 既定構成（ティアB=claude 有効）では confidential でも保護契約済み外部APIへ送信できる。
    [Fact]
    public async Task PostComplete_Confidential_RoutesToProtectedExternalAndSends()
    {
        var req = new { Prompt = "機密文書の要約", MaxTokens = 100, Confidentiality = "confidential", Purpose = "analysis" };
        var response = await factory.CreateClient().PostAsJsonAsync("/complete", req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>();
        body!.Sent.Should().BeTrue();
        body.Endpoint.Should().Be("claude-managed");
        body.Text.Should().NotBeNullOrWhiteSpace();
    }

    // FR-11: 送信先ティアが許容されない構成（confidential に対しティアCのみ）では送信を拒否する。
    [Fact]
    public async Task PostComplete_Confidential_WhenOnlyStandardExternal_IsRefused()
    {
        var client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
                s.Configure<LlmRoutingOptions>(o =>
                {
                    // 越境の許容されないティアC（標準外部API）のみへ差し替える。
                    o.AllowUnapprovedTierC = false;
                    o.Endpoints =
                    [
                        new LlmEndpointOptions
                        {
                            Name = "standard-external", Tier = ProtectionTier.C, Provider = "claude",
                            Enabled = true, Priority = 1, DefaultModel = "std", Models = ["std"]
                        }
                    ];
                }))).CreateClient();

        var req = new { Prompt = "機密文書の要約", MaxTokens = 100, Confidentiality = "confidential", Purpose = "analysis" };
        var response = await client.PostAsJsonAsync("/complete", req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>();
        body!.Sent.Should().BeFalse();          // 外部送信していない（縮退）
        body.RoutingReason.Should().Contain("拒否");
    }
}
