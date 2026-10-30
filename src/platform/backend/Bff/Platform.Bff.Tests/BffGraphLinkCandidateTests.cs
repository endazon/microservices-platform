using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Bff.Tests;

// FR-17, SC-19, ADR-0034 決定 2・8, [[IADR-0655]] (#2343): **リンク作成の補完候補の BFF の経路**
// （`GET /bff/graph/link-candidates` → 後段 `GET /graph/link-candidates` の透過中継）。
//
// 🔴 **ロールの門を置かない**（SC-19 は一般利用者の画面である）。絞り込みは後段が利用者の read スコープで行う。
// 🔴 **資格情報を伝播する** —— 切れると後段は anonymous として解決し、候補が静かに 0 件になる（陽性対照で固定する）。
// 🔴 **後段の状態と本文を詰め替えない**（`q` の 400 を本文ごと返す。件数を足さない）。
// 後段（GraphService）の HttpClient だけを本クラスで差し替える（共有の器は触らない）。
public class BffGraphLinkCandidateTests(BffTestFactory baseFactory) : IClassFixture<BffTestFactory>
{
    private sealed class Recorder : HttpMessageHandler
    {
        public string? LastPathAndQuery { get; private set; }
        public string? LastAuthorization { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } =
            """[{"documentId":"bbbbbbbb-0000-0000-0000-000000000002","title":"設計メモ","isPrivateNote":false}]""";
        public bool Throws { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastPathAndQuery = request.RequestUri?.PathAndQuery;
            LastAuthorization = request.Headers.Authorization?.ToString()
                ?? (request.Headers.TryGetValues("Authorization", out var v) ? string.Join(",", v) : null);
            if (Throws) throw new HttpRequestException("graph unreachable");
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private (WebApplicationFactory<Program> App, Recorder Graph) App()
    {
        var graph = new Recorder();
        var app = baseFactory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddHttpClient("GraphService").ConfigurePrimaryHttpMessageHandler(() => graph)));
        return (app, graph);
    }

    private static HttpClient Client(WebApplicationFactory<Program> app, string? roles = null)
    {
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        if (roles is not null) c.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return c;
    }

    // 陽性対照: 一般利用者でも引け、資格情報と検索語が後段へ届き、候補の配列がそのまま返る。
    [Fact]
    public async Task 一般利用者でも候補を引け資格情報と検索語が後段へ届く()
    {
        var (app, graph) = App();

        var res = await Client(app, "platform-user")
            .GetAsync("/bff/graph/link-candidates?q=%E8%A8%AD%E8%A8%88&limit=5", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        graph.LastPathAndQuery.Should().Be("/graph/link-candidates?q=%E8%A8%AD%E8%A8%88&limit=5");
        graph.LastAuthorization.Should().NotBeNullOrEmpty(
            "伝播しないと GraphService は利用者を anonymous として解決し、候補が静かに 0 件になる");
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().StartWith("[").And.Contain("設計メモ").And.NotContain("total");
    }

    // 件数を指定しなければ後段へ送らない（既定は後段が唯一の情報源）。
    [Fact]
    public async Task 件数を指定しなければ後段へ送らない()
    {
        var (app, graph) = App();

        await Client(app).GetAsync("/bff/graph/link-candidates?q=ab", TestContext.Current.CancellationToken);

        graph.LastPathAndQuery.Should().Be("/graph/link-candidates?q=ab");
    }

    // 後段の 400（検索語の長さ）は本文ごと透過する。
    [Fact]
    public async Task 後段の400は本文ごと透過する()
    {
        var (app, graph) = App();
        graph.Status = HttpStatusCode.BadRequest;
        graph.Body = """{"errors":{"q":["q は 2〜200 文字で指定してください。"]}}""";

        var res = await Client(app).GetAsync("/bff/graph/link-candidates?q=a", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("\"q\"");
    }

    [Fact]
    public async Task 未認証は401で後段へ行かない()
    {
        var (app, graph) = App();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");

        var res = await client.GetAsync("/bff/graph/link-candidates?q=ab", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        graph.LastPathAndQuery.Should().BeNull();
    }

    // 後段へ到達できなければ 502（空の候補へ縮退しない —— 「無い」と「引けない」を混ぜない）。
    [Fact]
    public async Task 後段へ到達できなければ502()
    {
        var (app, graph) = App();
        graph.Throws = true;

        var res = await Client(app).GetAsync("/bff/graph/link-candidates?q=ab", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }
}
