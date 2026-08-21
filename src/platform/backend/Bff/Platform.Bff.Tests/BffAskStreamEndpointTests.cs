using AwesomeAssertions;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Platform.Bff.Tests;

// IADR-0037, FR-04, UC-01, SC-01: /bff/analysis/ask/stream が上流 SSE を中継し、Authorization を伝播することを検証する。
public class BffAskStreamEndpointTests(BffTestFactory factory) : IClassFixture<BffTestFactory>
{
    [Fact]
    public async Task PostAskStream_RelaysUpstreamSseAndPropagatesAuthorization()
    {
        var client = factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/bff/analysis/ask/stream")
        {
            Content = JsonContent.Create(new { question = "経費規程は？" }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "token-abc");

        var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("event: citations");
        body.Should().Contain("event: token");
        body.Should().Contain("event: done");
        body.Should().Contain("回答");

        // FR-05: ABAC 権限解決のため Authorization が後段へ伝播される。
        factory.LastForwardedAuthorization.Should().Be("Bearer token-abc");
    }
}
