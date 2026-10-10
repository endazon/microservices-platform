using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using Knowledge.Bff.Endpoints;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Bff.Tests;

// NFR-16, FR-18, ADR-0117, ADR-0051 決定 4, [[IADR-0530]] 決定 1・2 (#1887):
// **計測専用の生成の口**（`POST /bff/graph/suggestions/generate/{documentId}`）の門を固定する。
//
// 🔴 3 つの門を陰陽の対で測る。
//   ① 構成の鍵（`Measurement:EnableSuggestionGenerate`）が無い・偽 → **ルート表に載らない**（404。後段へ行かない）
//   ② 鍵が真でも**システム管理者だけ**（運用者・一般利用者は 403。後段へ行かない）。未認証は 401
//   ③ 鍵が真で管理者 → 後段の生成の口へ**資格情報つきで** POST し、本文と状態コードを詰め替えずに返す
// ③ が陽性対照である。これが無いと ①② の否定形は「口がどこにも無い」実装でも緑になる。
public class BffMeasurementSuggestionGenerateTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffMeasurementSuggestionGenerateTests(BffTestFactory factory) => _factory = factory;

    private static readonly Guid SourceId = new("aaaaaaaa-0000-0000-0000-000000000011");

    private static string PathOf(Guid documentId) => $"/bff/graph/suggestions/generate/{documentId}";

    private WebApplicationFactory<Program> WithFlag(string? value) =>
        value is null
            ? _factory
            : _factory.WithWebHostBuilder(b =>
                b.UseSetting(GraphBffEndpoints.MeasurementSuggestionGenerateKey, value));

    private static HttpClient Client(WebApplicationFactory<Program> factory, string? roles = null)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        if (roles is not null)
            c.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return c;
    }

    private static AiSuggestionDto TagSuggestion() => new(
        Guid.NewGuid(), "tag", SourceId, null, null, "経費",
        "経費精算の手順を扱う", "pending", 0, null, "経費精算規程 v3.2", null);

    // ① 陰: 既定（鍵なし）と明示の false では口がルート表に載らない。後段へ 1 度も行かない。
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task 鍵が無いか偽なら口は無く404で後段へ行かない(string? flag)
    {
        var documentId = Guid.NewGuid();
        var factory = WithFlag(flag);

        var res = await Client(factory).PostAsync(PathOf(documentId), null, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (_factory.LastGraphPath ?? string.Empty).Should().NotContain(documentId.ToString(),
            "口が無いので後段の生成は呼ばれない（LLM の費用も出ない）");
        RoutePatterns(factory).Should().NotContain(p => p.Contains("generate", StringComparison.OrdinalIgnoreCase),
            "既定の構成では生成の口はルート表に載らない（製品の口ではない）");
    }

    // ③ 陽性対照: 鍵が真で管理者なら、後段の生成の口へ POST し、資格情報が届き、本文が素通りする。
    [Fact]
    public async Task 鍵が真なら管理者は後段の生成の口へ資格情報つきで届き本文が素通りする()
    {
        var documentId = Guid.NewGuid();
        var factory = WithFlag("true");
        _factory.GraphStubStatusCode = HttpStatusCode.OK;
        _factory.StubAiSuggestions = [TagSuggestion()];

        var res = await Client(factory).PostAsync(PathOf(documentId), null, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastGraphPath.Should().Be($"/graph/suggestions/generate/{documentId}");
        _factory.LastGraphMethod.Should().Be("POST");
        _factory.LastGraphForwardedAuthorization.Should().NotBeNullOrEmpty(
            "後段は利用者の ABAC スコープで生成する（1 実行 = 1 利用者のスコープ。資格情報が無いと 401）");
        var items = await res.Content.ReadFromJsonAsync<List<AiSuggestionDto>>(TestContext.Current.CancellationToken);
        items!.Single().TagValue.Should().Be("経費", "保留中のタグ提案は承認（D-2）の材料になる");
        RoutePatterns(factory).Should().Contain("/bff/graph/suggestions/generate/{documentId:guid}");
    }

    // ② 陰: 鍵が真でも、システム管理者以外は 403。後段へ行かない。
    [Theory]
    [InlineData("platform-operator")]
    [InlineData("platform-user")]
    public async Task 鍵が真でも管理者以外は403で後段へ行かない(string role)
    {
        var documentId = Guid.NewGuid();

        var res = await Client(WithFlag("true"), role)
            .PostAsync(PathOf(documentId), null, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (_factory.LastGraphPath ?? string.Empty).Should().NotContain(documentId.ToString());
    }

    // ② 陰: 未認証は BFF の入口で 401。
    [Fact]
    public async Task 鍵が真でも未認証は401()
    {
        var client = WithFlag("true").CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");

        var res = await client.PostAsync(PathOf(Guid.NewGuid()), null, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // 後段の 404（起点が見えない・存在しない）は詰め替えずに返す（存在秘匿を BFF 層で破らない）。
    [Fact]
    public async Task 後段の404はそのまま返す()
    {
        _factory.GraphStubStatusCode = HttpStatusCode.NotFound;
        try
        {
            var res = await Client(WithFlag("true"))
                .PostAsync(PathOf(Guid.NewGuid()), null, TestContext.Current.CancellationToken);

            res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            _factory.GraphStubStatusCode = HttpStatusCode.OK;
        }
    }

    private static List<string> RoutePatterns(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IEnumerable<EndpointDataSource>>()
            .SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .ToList();
    }
}
