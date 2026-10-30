using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Bff.Tests;

// FR-18, UC-10, SC-03, ADR-0081 決定 1・2, ADR-0034 決定 2, [[IADR-0653]] (#2106):
// **AI 提案の生成の製品の口**（`POST /bff/graph/suggestions/generate/{documentId}`）。
//
// 🔴 **押せるのはその文書を読める利用者**（決定 2）—— 管理者・運用者に限らない。資格は後段が read で判定し、
// 見えない・無い起点は 404（BFF は詰め替えない。403 にしない）。BFF はロールの門を置かない。
// 🔴 **構成の鍵に依らず開いている**（IADR-0530 決定 1 の計測専用の口は撤去した）。
// 陽性対照（一般利用者の要求が資格情報つきで後段へ届く）が無いと、否定形は「口がどこにも無い」実装でも緑になる。
public class BffGraphSuggestionGenerateTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffGraphSuggestionGenerateTests(BffTestFactory factory) => _factory = factory;

    private static readonly Guid SourceId = new("aaaaaaaa-0000-0000-0000-000000000021");

    private static string PathOf(Guid documentId) => $"/bff/graph/suggestions/generate/{documentId}";

    private HttpClient Client(string? roles, HttpStatusCode status, string? body)
    {
        _factory.GraphWriteStubStatusCode = status;
        _factory.GraphWriteStubBody = body;
        _factory.GraphWriteStubThrows = false;
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        if (roles is not null)
            c.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return c;
    }

    private static string Created() => JsonSerializer.Serialize(new List<AiSuggestionDto>
    {
        new(Guid.NewGuid(), "tag", SourceId, null, null, "経費",
            "経費精算の手順を扱う", "pending", 0, null, "経費精算規程 v3.2", null),
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    // 陽性対照: 一般利用者・運用者・管理者のいずれも、既定の構成（鍵なし）で後段の生成の口へ資格情報つきで届き、
    // 作れた提案の配列が素通りする。
    [Theory]
    [InlineData("platform-user")]
    [InlineData("platform-operator")]
    [InlineData("platform-admin")]
    public async Task 文書を読める利用者は既定の構成で生成でき資格情報が後段へ届く(string role)
    {
        var documentId = Guid.NewGuid();

        var res = await Client(role, HttpStatusCode.OK, Created())
            .PostAsync(PathOf(documentId), null, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastGraphPath.Should().Be($"/graph/suggestions/generate/{documentId}");
        _factory.LastGraphMethod.Should().Be("POST");
        _factory.LastGraphForwardedAuthorization.Should().NotBeNullOrEmpty(
            "後段は押した利用者の ABAC スコープで生成する（1 実行 = 1 利用者のスコープ）");
        var items = await res.Content.ReadFromJsonAsync<List<AiSuggestionDto>>(TestContext.Current.CancellationToken);
        items!.Single().TagValue.Should().Be("経費");
    }

    // 構成の鍵に依らない: 製品の口はルート表に常に載る。計測専用の口（管理者限定）は無い。
    [Fact]
    public void 製品の生成の口は常にルート表に載り管理者限定の口は無い()
    {
        using var scope = _factory.Services.CreateScope();
        var endpoints = scope.ServiceProvider.GetRequiredService<IEnumerable<EndpointDataSource>>()
            .SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => (e.RoutePattern.RawText ?? string.Empty).Contains("generate", StringComparison.OrdinalIgnoreCase))
            .ToList();

        endpoints.Select(e => e.RoutePattern.RawText).Should().Equal(["/bff/graph/suggestions/generate/{documentId:guid}"]);
        endpoints.Single().Metadata.OfType<EndpointNameMetadata>().Single().EndpointName
            .Should().Be("BffGraphSuggestionGenerate");
    }

    // 🔴 文書を読めない（起点が見えない・無い）は後段の 404 のまま返す。403 にしない（存在秘匿）。
    [Fact]
    public async Task 文書を読めない利用者は後段の404のまま返る()
    {
        var res = await Client("platform-user", HttpStatusCode.NotFound, null)
            .PostAsync(PathOf(Guid.NewGuid()), null, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _factory.LastGraphPath.Should().StartWith("/graph/suggestions/generate/",
            "陽性対照: 404 は後段から来ている（BFF の経路が無いのではない）");
    }

    // 未認証は BFF の入口で 401。後段へ行かない。
    [Fact]
    public async Task 未認証は401で後段へ行かない()
    {
        var documentId = Guid.NewGuid();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");

        var res = await client.PostAsync(PathOf(documentId), null, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (_factory.LastGraphPath ?? string.Empty).Should().NotContain(documentId.ToString());
    }

    // 後段へ到達できないときは 502（成功へ縮退しない）。
    [Fact]
    public async Task 後段へ到達できなければ502()
    {
        var client = Client("platform-user", HttpStatusCode.OK, null);
        _factory.GraphWriteStubThrows = true;

        var res = await client.PostAsync(PathOf(Guid.NewGuid()), null, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }
}
