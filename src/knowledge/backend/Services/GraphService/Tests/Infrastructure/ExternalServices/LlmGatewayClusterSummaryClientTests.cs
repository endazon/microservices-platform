using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using GraphService.Domain.Clustering;
using GraphService.Infrastructure.ExternalServices;
using Knowledge.Contracts.Dtos;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphService.Tests.Infrastructure.ExternalServices;

// FR-11, FR-17, FR-18, ADR-0010, ADR-0035 決定 3, [[IADR-0266]] 決定 6, [[IADR-0430]] 決定 1・4 (#1395):
// クラスタ要約のゲートウェイ呼び出しの**写像**を `HttpMessageHandler` 層で固定する。
[Trait("TestKind", "Unit")]
public class LlmGatewayClusterSummaryClientTests
{
    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }
        public string? LastPath { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri!.AbsolutePath;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private static LlmGatewayClusterSummaryClient Client(HttpMessageHandler handler)
        => new(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://llm-gateway/") },
            NullLogger<LlmGatewayClusterSummaryClient>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode code, object body)
        => new(code)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };

    private static ClusterSummaryPrompt Prompt(string tier = ConfidentialityLevels.Internal)
        => ClusterSummaryPrompt.Seal(Guid.NewGuid(), tier, [
            new ClusterMemberDocument(Guid.NewGuid(), "設計メモ",
                new Dictionary<string, string> { [ConfidentialityLevels.AttributeKey] = tier }),
        ])!;

    // 🔴 (T-8): **用途名は `graph-cluster-summary` である。** AI 提案（`graph-suggestion`）と
    // 同じ値にすると、用途別の費用集計で 2 経路を区別できなくなる。機密区分も封の値をそのまま渡す。
    [Fact]
    public async Task 用途名と機密区分を添えてcompleteを呼ぶ()
    {
        var handler = new CapturingHandler(_ => Json(HttpStatusCode.OK, new
        {
            text = "この集まりは設計メモである。",
            model = "claude-opus-5",
            inputTokens = 10,
            outputTokens = 20,
            sent = true,
        }));

        var summary = await Client(handler).SummarizeAsync(
            Prompt(ConfidentialityLevels.Confidential), TestContext.Current.CancellationToken);

        summary.Should().Be("この集まりは設計メモである。");
        handler.LastPath.Should().Be("/complete");
        var body = JsonDocument.Parse(handler.LastBody!).RootElement;
        body.GetProperty("purpose").GetString().Should().Be("graph-cluster-summary");
        body.GetProperty("confidentiality").GetString().Should().Be(ConfidentialityLevels.Confidential);
        // 🔴 モデルは指定しない（宛先の決定はゲートウェイの経路決定が持つ）。
        (!body.TryGetProperty("model", out var model) || model.ValueKind == JsonValueKind.Null)
            .Should().BeTrue("呼び出し側がモデルを固定すると越境判定が選んだ宛先と食い違い得る");
    }

    // 🔴 (T-9): [[IADR-0266]] 決定 6 —— **縮退した応答を根拠に使わない。**
    // `Sent=false`（越境させていない）・`refusal`（モデルが拒否した）・空文字はいずれも
    // 「採れなかった」であり、**要約として書いてはならない**。
    [Theory]
    [InlineData(false, null, "本文", "Sent=false は機密区分による送信拒否である")]
    [InlineData(true, "refusal", "本文", "refusal は送信は成立したがモデルが拒否した")]
    [InlineData(true, "end_turn", "", "空の要約を書くと中身の無い行が残る")]
    [InlineData(true, "end_turn", "   ", "空白だけの要約も同じ")]
    public async Task 縮退した応答は要約として採らない(
        bool sent, string? stopReason, string text, string because)
    {
        var handler = new CapturingHandler(_ => Json(HttpStatusCode.OK, new
        {
            text,
            model = "claude-opus-5",
            inputTokens = 10,
            outputTokens = 0,
            sent,
            stopReason,
        }));

        var summary = await Client(handler).SummarizeAsync(
            Prompt(), TestContext.Current.CancellationToken);

        summary.Should().BeNull(because);
    }

    // (T-9): 非 2xx でも例外にしない（1 区分の失敗はそのクラスタを未要約に残すだけである）。
    [Fact]
    public async Task 非2xxでも例外にせずnullへ落とす()
    {
        var handler = new CapturingHandler(_ => Json(HttpStatusCode.ServiceUnavailable, new { }));

        var summary = await Client(handler).SummarizeAsync(
            Prompt(), TestContext.Current.CancellationToken);

        summary.Should().BeNull();
    }

    // (T-9): 不達（HttpRequestException）も同じ枝へ落とす。
    [Fact]
    public async Task 不達でも例外にせずnullへ落とす()
    {
        var handler = new CapturingHandler(_ => throw new HttpRequestException("unreachable"));

        var summary = await Client(handler).SummarizeAsync(
            Prompt(), TestContext.Current.CancellationToken);

        summary.Should().BeNull();
    }
}
