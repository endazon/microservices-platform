using FluentAssertions;
using System.Net;
using System.Net.Http.Json;

namespace AiAnalysisService.Api.Tests;

// IADR-0037, FR-04, UC-01: /analysis/ask/stream が SSE で citations → token* → done を送ることを検証する。
public class AskStreamEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task PostAskStream_EmitsCitationsThenTokensThenDone()
    {
        var resp = await factory.CreateClient()
            .PostAsJsonAsync("/analysis/ask/stream", new { Question = "経費規程は？" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

        var body = await resp.Content.ReadAsStringAsync();

        // 出典が本文より先に送られる。
        var citationsIdx = body.IndexOf("event: citations", StringComparison.Ordinal);
        var tokenIdx = body.IndexOf("event: token", StringComparison.Ordinal);
        var doneIdx = body.IndexOf("event: done", StringComparison.Ordinal);

        citationsIdx.Should().BeGreaterThanOrEqualTo(0);
        tokenIdx.Should().BeGreaterThan(citationsIdx);   // citations が token より前
        doneIdx.Should().BeGreaterThan(tokenIdx);        // done が最後
        body.Should().Contain("文書A");                  // 出典
        body.Should().Contain("回答 [1]");                // 本文トークン
        body.Should().Contain("answerId");               // done ペイロード（フィードバック紐付け）
    }
}
