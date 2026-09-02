using System.Net;
using System.Text;
using DataSourceService.Infrastructure.ExternalServices;
using DataSourceService.Domain;
using DataSourceService.Domain.Ports;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataSourceService.Tests.Infrastructure.ExternalServices;

// FR-01, UC-04, 09_datasource-connectors（優先2: Wiki）, IADR-0053, Issue #217:
// Wiki コネクタ（汎用 REST 契約）の単体テスト。fake HttpMessageHandler で応答を模す（実サーバ不要＝CI 緑）。
// 実 Wiki 製品に対する結合検証は実コンテナ前提の follow-up（本テスト対象外）。
public sealed class WikiConnectorTests
{
    private const string Base = "https://wiki.example.com";

    private static DataSource WikiSource(Dictionary<string, string>? config = null)
        => DataSource.Create("wiki", "wiki", Base, config);

    private static WikiConnector Connector(StubHandler handler)
        => new(new StubFactory(handler), NullLogger<WikiConnector>.Instance);

    [Fact]
    public async Task Discover_FullScan_ReturnsAllPages()
    {
        var handler = ListResponder("""
            [
              {"id":"p1","title":"A","updatedAt":"2026-07-01T00:00:00Z"},
              {"id":"p2","title":"B","updatedAt":"2026-07-05T00:00:00Z"}
            ]
            """);
        var items = await Connector(handler).DiscoverAsync(WikiSource(), since: null, CancellationToken.None);

        items.Select(i => i.Path).Should().BeEquivalentTo("p1", "p2");
    }

    [Fact]
    public async Task Discover_Incremental_ExcludesPagesAtOrBeforeWatermark()
    {
        var handler = ListResponder("""
            [
              {"id":"old","updatedAt":"2026-07-01T00:00:00Z"},
              {"id":"new","updatedAt":"2026-07-09T00:00:00Z"}
            ]
            """);
        var since = DateTimeOffset.Parse("2026-07-05T00:00:00Z");

        var items = await Connector(handler).DiscoverAsync(WikiSource(), since, CancellationToken.None);

        items.Select(i => i.Path).Should().ContainSingle().Which.Should().Be("new");
    }

    [Fact]
    public async Task Fetch_ReturnsBytesAndContentType()
    {
        var handler = new StubHandler
        {
            Responder = req => req.RequestUri!.AbsolutePath.Contains("content")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("# Body", Encoding.UTF8, "text/markdown") }
                : new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        var raw = await Connector(handler).FetchAsync(
            WikiSource(), new SourceItem("p1", DateTimeOffset.UtcNow, 0), CancellationToken.None);

        Encoding.UTF8.GetString(raw.Bytes).Should().Be("# Body");
        raw.ContentType.Should().Be("text/markdown");
    }

    [Fact]
    public async Task Discover_SendsBearerToken_FromConfig()
    {
        var handler = ListResponder("[]");
        var source = WikiSource(new Dictionary<string, string> { ["apiToken"] = "secret-123" });

        await Connector(handler).DiscoverAsync(source, null, CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.Requests[0].Headers.Authorization!.Parameter.Should().Be("secret-123");
    }

    [Fact]
    public async Task Discover_UsesConfiguredListPath()
    {
        var handler = ListResponder("[]");
        var source = WikiSource(new Dictionary<string, string> { ["listPath"] = "/rest/pages" });

        await Connector(handler).DiscoverAsync(source, null, CancellationToken.None);

        handler.Requests[0].RequestUri!.ToString().Should().Be($"{Base}/rest/pages");
    }

    [Fact]
    public async Task Discover_HttpFailure_Throws()
    {
        var handler = new StubHandler { Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };

        var act = () => Connector(handler).DiscoverAsync(WikiSource(), null, CancellationToken.None);

        // HTTP 失敗は例外 → オーケストレータが discover 失敗として watermark 非前進・アラートに載せる（IADR-0051 決定3a）。
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Discover_MissingConnectionUri_ReturnsEmpty()
    {
        var handler = ListResponder("[]");
        var source = DataSource.Create("wiki", "wiki", ""); // ConnectionUri 未設定

        var items = await Connector(handler).DiscoverAsync(source, null, CancellationToken.None);

        items.Should().BeEmpty();
        handler.Requests.Should().BeEmpty("接続先未設定なら HTTP 呼び出しをしない");
    }

    // ---- test doubles ----------------------------------------------------

    private static StubHandler ListResponder(string json) => new()
    {
        Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        },
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; }
            = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Responder(request));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
