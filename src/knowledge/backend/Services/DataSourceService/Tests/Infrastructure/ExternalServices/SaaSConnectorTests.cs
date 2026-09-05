using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DataSourceService.Infrastructure.ExternalServices;
using DataSourceService.Domain;
using DataSourceService.Domain.Ports;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataSourceService.Tests.Infrastructure.ExternalServices;

// FR-01, UC-04, 09_datasource-connectors（優先3: SaaS）, IADR-0054, Issue #218:
// SaaS コネクタ（汎用 REST 契約）の単体テスト。fake HttpMessageHandler で応答を模す（実 API 不要＝CI 緑）。
// 429 は Retry-After: 0 で待機ゼロにし高速化。実 SaaS API 結合は実 API/コンテナ前提の follow-up（本テスト対象外）。
[Trait("TestKind", "Unit")]
public sealed class SaaSConnectorTests
{
    private const string Base = "https://saas.example.com";

    private static DataSource SaasSource(Dictionary<string, string>? config = null)
        => DataSource.Create("saas", "saas", Base, config);

    private static SaaSConnector Connector(StubHandler handler)
        => new(new StubFactory(handler), NullLogger<SaaSConnector>.Instance);

    [Fact]
    public async Task Discover_AggregatesAllPages_ByCursor()
    {
        var handler = new StubHandler
        {
            Responder = (req, _) => req.RequestUri!.ToString().Contains("cursor=c2")
                ? Json("""{"items":[{"id":"b","updatedAt":"2026-07-05T00:00:00Z"}],"nextCursor":null}""")
                : Json("""{"items":[{"id":"a","updatedAt":"2026-07-01T00:00:00Z"}],"nextCursor":"c2"}"""),
        };

        var items = await Connector(handler).DiscoverAsync(SaasSource(), since: null, CancellationToken.None);

        items.Select(i => i.Path).Should().BeEquivalentTo("a", "b");
        handler.Requests.Should().HaveCount(2, "カーソルが尽きるまで全ページをたどる");
    }

    [Fact]
    public async Task Discover_Incremental_ExcludesPagesAtOrBeforeWatermark()
    {
        var handler = new StubHandler
        {
            Responder = (_, _) => Json("""
                {"items":[
                  {"id":"old","updatedAt":"2026-07-01T00:00:00Z"},
                  {"id":"new","updatedAt":"2026-07-09T00:00:00Z"}
                ],"nextCursor":null}
                """),
        };
        var since = DateTimeOffset.Parse("2026-07-05T00:00:00Z");

        var items = await Connector(handler).DiscoverAsync(SaasSource(), since, CancellationToken.None);

        items.Select(i => i.Path).Should().ContainSingle().Which.Should().Be("new");
    }

    [Fact]
    public async Task Discover_RetriesOn429_ThenSucceeds()
    {
        var handler = new StubHandler
        {
            Responder = (_, idx) => idx == 0
                ? TooManyRequests()
                : Json("""{"items":[{"id":"x","updatedAt":"2026-07-01T00:00:00Z"}],"nextCursor":null}"""),
        };

        var items = await Connector(handler).DiscoverAsync(SaasSource(), null, CancellationToken.None);

        items.Select(i => i.Path).Should().ContainSingle().Which.Should().Be("x");
        handler.Requests.Should().HaveCount(2, "429 の後に Retry-After 従い再試行して成功する");
    }

    [Fact]
    public async Task Discover_429ExceedsMaxRetries_Throws()
    {
        var handler = new StubHandler { Responder = (_, _) => TooManyRequests() };
        var source = SaasSource(new Dictionary<string, string> { ["maxRetries"] = "1" });

        var act = () => Connector(handler).DiscoverAsync(source, null, CancellationToken.None);

        // 429 上限超過は例外 → オーケストレータが watermark 非前進・継続失敗アラートに載せる（IADR-0051 決定3a）。
        await act.Should().ThrowAsync<HttpRequestException>();
        handler.Requests.Should().HaveCount(2, "maxRetries=1 なら 1 回再試行して打ち切る");
    }

    [Fact]
    public async Task Fetch_RetriesOn429_ThenSucceeds()
    {
        var handler = new StubHandler
        {
            Responder = (_, idx) => idx == 0
                ? TooManyRequests()
                : new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("# Doc", Encoding.UTF8, "text/markdown") },
        };

        var raw = await Connector(handler).FetchAsync(
            SaasSource(), new SourceItem("x1", DateTimeOffset.UtcNow, 0), CancellationToken.None);

        Encoding.UTF8.GetString(raw.Bytes).Should().Be("# Doc");
        handler.Requests.Should().HaveCount(2, "Fetch も 429 の後に再試行して成功する");
    }

    [Fact]
    public async Task Fetch_429ExceedsMaxRetries_Throws()
    {
        var handler = new StubHandler { Responder = (_, _) => TooManyRequests() };
        var source = SaasSource(new Dictionary<string, string> { ["maxRetries"] = "1" });

        var act = () => Connector(handler).FetchAsync(
            source, new SourceItem("x1", DateTimeOffset.UtcNow, 0), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Fetch_ReturnsBytesAndContentType()
    {
        var handler = new StubHandler
        {
            Responder = (req, _) => req.RequestUri!.AbsolutePath.Contains("/items/")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("# Doc", Encoding.UTF8, "text/markdown") }
                : new HttpResponseMessage(HttpStatusCode.NotFound),
        };

        var raw = await Connector(handler).FetchAsync(
            SaasSource(), new SourceItem("x1", DateTimeOffset.UtcNow, 0), CancellationToken.None);

        Encoding.UTF8.GetString(raw.Bytes).Should().Be("# Doc");
        raw.ContentType.Should().Be("text/markdown");
    }

    [Fact]
    public async Task Discover_SendsBearerToken_FromConfig()
    {
        var handler = new StubHandler { Responder = (_, _) => Json("""{"items":[],"nextCursor":null}""") };
        var source = SaasSource(new Dictionary<string, string> { ["apiToken"] = "saas-secret" });

        await Connector(handler).DiscoverAsync(source, null, CancellationToken.None);

        handler.Requests[0].Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.Requests[0].Headers.Authorization!.Parameter.Should().Be("saas-secret");
    }

    [Fact]
    public async Task Discover_MissingConnectionUri_ReturnsEmpty()
    {
        var handler = new StubHandler { Responder = (_, _) => Json("""{"items":[],"nextCursor":null}""") };
        var source = DataSource.Create("saas", "saas", "");

        var items = await Connector(handler).DiscoverAsync(source, null, CancellationToken.None);

        items.Should().BeEmpty();
        handler.Requests.Should().BeEmpty();
    }

    // FR-05, UC-04, ADR-0036, ADR-0074, Issue #752: **更新者を運ぶ。**（wiki と同型）
    [Fact]
    public async Task Discover_CarriesUpdatedBy_FromTheDefaultField()
    {
        var handler = new StubHandler
        {
            Responder = (_, _) => Json("""
                {"items":[{"id":"x1","updatedAt":"2026-07-01T00:00:00Z","updatedBy":"hr-tanaka"}],"nextCursor":null}
                """),
        };

        var items = await Connector(handler).DiscoverAsync(SaasSource(), null, CancellationToken.None);

        items.Should().ContainSingle().Which.UpdatedBy.Should().Be("hr-tanaka");
    }

    [Fact]
    public async Task Discover_UsesConfiguredUpdatedByField()
    {
        var handler = new StubHandler
        {
            Responder = (_, _) => Json("""
                {"items":[{"id":"x1","updatedAt":"2026-07-01T00:00:00Z","author":"alice"}],"nextCursor":null}
                """),
        };
        var source = SaasSource(new Dictionary<string, string> { ["updatedByField"] = "author" });

        var items = await Connector(handler).DiscoverAsync(source, null, CancellationToken.None);

        items.Should().ContainSingle().Which.UpdatedBy.Should().Be("alice");
    }

    // 🔴 陰性: 「項目が無い」も「在るが空」も null になり、`owner` は予約値へ倒れる。
    [Theory]
    [InlineData("""{"items":[{"id":"x1","updatedAt":"2026-07-01T00:00:00Z"}],"nextCursor":null}""")]
    [InlineData("""{"items":[{"id":"x1","updatedAt":"2026-07-01T00:00:00Z","updatedBy":"  "}],"nextCursor":null}""")]
    public async Task Discover_LeavesUpdatedByNull_WhenTheSourceDoesNotCarryAUsableValue(string json)
    {
        var handler = new StubHandler { Responder = (_, _) => Json(json) };

        var items = await Connector(handler).DiscoverAsync(SaasSource(), null, CancellationToken.None);

        items.Should().ContainSingle().Which.UpdatedBy.Should().BeNull();
    }

    // ---- test doubles ----------------------------------------------------

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage TooManyRequests()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        // Retry-After: 0 → 待機ゼロで即再試行（テストを高速化）。
        resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        return resp;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private int _calls;
        public Func<HttpRequestMessage, int, HttpResponseMessage> Responder { get; set; }
            = (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound);
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var index = _calls++;
            Requests.Add(request);
            return Task.FromResult(Responder(request, index));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
