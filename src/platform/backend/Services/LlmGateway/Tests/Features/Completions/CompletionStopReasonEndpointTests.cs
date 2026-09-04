using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using LlmGateway.Domain.Ports;
using Platform.Shared.Contracts.Dtos;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LlmGateway.Tests.Features.Completions;

// FR-11, ADR-0025, IADR-0104 (#379): /complete・/complete/stream が終了理由（stopReason）を
// 応答契約に載せ、呼び出し側が「モデルが拒否した」と「送信したが空応答」を区別できることを固定する。
// 拒否は外部送信が成立したうえでの応答であるため sent=true のままとし（越境監査・課金の意味を守る）、
// 区別は stopReason で行う。
// IADR-0110 (#395): メトリクス購読テスト（CompletionMetricsTests）と直列化する。
[Collection(CompletionEndpointCollection.Name)]
[Trait("TestKind", "Integration")]
public class CompletionStopReasonEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private record CompletionResponse(
        string Text, string Model, int InputTokens, int OutputTokens,
        bool Sent, string? Endpoint, string? RoutingReason, string? StopReason);

    private HttpClient ClientReturning(CompletionResult result) =>
        factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ILlmProvider>();
                var provider = new FixedResultProvider(result);
                s.AddKeyedSingleton<ILlmProvider>("claude", provider);
                s.AddKeyedSingleton<ILlmProvider>("selfhosted", provider);
                s.AddKeyedSingleton<ILlmProvider>("copilot", provider);
            })).CreateClient();

    // T-16: 拒否は sent=true・text 空・stopReason="refusal" で返る。
    // sent=false にしないのは、拒否が「外部へ送信し、モデルが応答した」事象だからである（IADR-0104 §決定）。
    [Fact]
    public async Task PostComplete_WhenProviderRefuses_ReportsRefusalAndKeepsSentTrue()
    {
        var client = ClientReturning(new CompletionResult("", 11, 0, CompletionStopReasons.Refusal));

        var req = new { Prompt = "危険な要求", MaxTokens = 100, Confidentiality = "public", Purpose = "default" };
        var response = await client.PostAsJsonAsync("/complete", req, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>(TestContext.Current.CancellationToken);
        body!.StopReason.Should().Be("refusal");
        body.Sent.Should().BeTrue();      // 越境は成立している（監査上の意味を変えない）
        body.Text.Should().BeEmpty();     // 未改修の呼び出し側は空本文で安全側へ倒れる
    }

    // T-17: 上限到達も stopReason で区別できる（本文が空になる点は拒否と同じ）。
    [Fact]
    public async Task PostComplete_WhenMaxTokens_ReportsMaxTokensStopReason()
    {
        var client = ClientReturning(new CompletionResult("", 11, 100, CompletionStopReasons.MaxTokens));

        var req = new { Prompt = "長文の要約", MaxTokens = 100, Confidentiality = "public", Purpose = "default" };
        var response = await client.PostAsJsonAsync("/complete", req, TestContext.Current.CancellationToken);

        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>(TestContext.Current.CancellationToken);
        body!.StopReason.Should().Be("max_tokens");
        body.Sent.Should().BeTrue();
    }

    // T-18: 正常終了では本文・トークン数が従来どおりで stopReason が透過する（回帰防止）。
    [Fact]
    public async Task PostComplete_WhenEndTurn_ReturnsBodyWithStopReason()
    {
        var client = ClientReturning(new CompletionResult("回答本文", 11, 22, CompletionStopReasons.EndTurn));

        var req = new { Prompt = "要約して", MaxTokens = 100, Confidentiality = "public", Purpose = "default" };
        var response = await client.PostAsJsonAsync("/complete", req, TestContext.Current.CancellationToken);

        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>(TestContext.Current.CancellationToken);
        body!.StopReason.Should().Be("end_turn");
        body.Text.Should().Be("回答本文");
        body.OutputTokens.Should().Be(22);
    }

    // FR-11: egress 拒否（送信していない）は従来どおり sent=false で、stopReason は付かない。
    // 「送っていない」と「送ったがモデルが拒否した」を取り違えないことの担保。
    [Fact]
    public async Task PostComplete_WhenEgressDenied_HasNoStopReason()
    {
        var client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
                s.Configure<LlmGateway.Domain.Routing.LlmRoutingOptions>(o =>
                {
                    o.AllowUnapprovedTierC = false;
                    o.Endpoints =
                    [
                        new LlmGateway.Domain.Routing.LlmEndpointOptions
                        {
                            Name = "standard-external", Tier = LlmGateway.Domain.Routing.ProtectionTier.C,
                            Provider = "claude", Enabled = true, Priority = 1,
                            DefaultModel = "std", Models = ["std"]
                        }
                    ];
                }))).CreateClient();

        var req = new { Prompt = "機密文書の要約", MaxTokens = 100, Confidentiality = "confidential", Purpose = "analysis" };
        var response = await client.PostAsJsonAsync("/complete", req, TestContext.Current.CancellationToken);

        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>(TestContext.Current.CancellationToken);
        body!.Sent.Should().BeFalse();
        body.StopReason.Should().BeNull();
    }

    // T-16: SSE の最終イベント（done）にも stopReason を載せる（IADR-0037 の経路）。
    // 送出済みデルタは撤回できないため、呼び出し側が done の理由を見て破棄できることが要件。
    [Fact]
    public async Task PostCompleteStream_WhenProviderRefuses_DoneEventCarriesRefusal()
    {
        var client = ClientReturning(new CompletionResult("", 11, 0, CompletionStopReasons.Refusal));

        var req = new { Prompt = "危険な要求", MaxTokens = 100, Confidentiality = "public", Purpose = "rag-answer" };
        var response = await client.PostAsJsonAsync("/complete/stream", req, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        text.Should().Contain("\"done\":true");
        text.Should().Contain("\"stopReason\":\"refusal\"");
        text.Should().Contain("\"sent\":true");
    }

    // 固定の CompletionResult を返すスタブ。ILlmProvider 既定の StreamAsync（1 チャンク縮退）を
    // そのまま使うため、stopReason が既定実装でも最終チャンクへ伝わることを併せて固定する。
    private sealed class FixedResultProvider(CompletionResult result) : ILlmProvider
    {
        public Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
            => Task.FromResult(result);
    }
}
