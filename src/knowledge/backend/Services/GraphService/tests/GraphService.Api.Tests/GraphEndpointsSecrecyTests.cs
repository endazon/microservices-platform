using System.Net;
using AwesomeAssertions;
using GraphService.Api.Foundation.Domain;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Api.Tests;

// FR-17, UC-10, ADR-0034 決定 2: **存在秘匿を固定する。**
//
// ADR-0034 は「利用者がリンク切れと権限不足を区別できないこと」を受け入れ済みの副作用として
// 明記している。区別できてしまうと、**権限外文書の存在そのものが漏れる**。
//
// 本テストは 3 つの経路（権限なし / 複製なし / 文書なし）が**応答として区別できない**ことを見る。
public class GraphEndpointsSecrecyTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public GraphEndpointsSecrecyTests(TestWebApplicationFactory factory) => _factory = factory;

    private static AccessScopeResponse InternalOnly()
        => new("test-user", [new AttributeFilter("confidentiality", ["internal"])], true);

    [Fact]
    public async Task Unauthorized_missing_and_nonexistent_are_indistinguishable()
    {
        var visible = Guid.NewGuid();
        var forbidden = Guid.NewGuid();
        var noAttributes = Guid.NewGuid();
        var nonexistent = Guid.NewGuid();

        await _factory.SeedAsync(db =>
        {
            db.Documents.Add(GraphDocument.Create(visible, "visible",
                new Dictionary<string, string> { ["confidentiality"] = "internal" },
                null, DateTimeOffset.UtcNow));
            db.Documents.Add(GraphDocument.Create(forbidden, "forbidden",
                new Dictionary<string, string> { ["confidentiality"] = "restricted" },
                null, DateTimeOffset.UtcNow));
            // 属性の複製がまだ届いていないノード（IADR-0241 決定 12-3 で不可視）。
            db.Documents.Add(GraphDocument.Create(noAttributes, "not-yet-synced",
                [], null, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        });

        _factory.ScopeProvider = _ => InternalOnly();
        var client = _factory.CreateClient();

        // 対照群: 見えるものは見える（テストが「全部 404」で空振りしていないことの担保）。
        var ok = await client.GetAsync($"/graph/{visible}", TestContext.Current.CancellationToken);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);

        var responses = new List<HttpResponseMessage>
        {
            await client.GetAsync($"/graph/{forbidden}", TestContext.Current.CancellationToken),
            await client.GetAsync($"/graph/{noAttributes}", TestContext.Current.CancellationToken),
            await client.GetAsync($"/graph/{nonexistent}", TestContext.Current.CancellationToken),
        };

        foreach (var r in responses)
            r.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 本文が 1 バイトも違わないこと。
        var bodies = new List<string>();
        foreach (var r in responses)
            bodies.Add(await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        bodies.Distinct().Should().HaveCount(1,
            "応答本文に差があると、そこから存在の有無が読める");

        // Content-Type にも差が出ないこと。
        responses.Select(r => r.Content.Headers.ContentType?.ToString() ?? "")
            .Distinct().Should().HaveCount(1);
    }

    // FR-05: 許可ポリシーが無ければ起点自体も見せない（deny-by-default）。
    [Fact]
    public async Task Returns_404_not_403_when_scope_is_not_granted()
    {
        var doc = Guid.NewGuid();
        await _factory.SeedAsync(db =>
        {
            db.Documents.Add(GraphDocument.Create(doc, "d",
                new Dictionary<string, string> { ["confidentiality"] = "public" },
                null, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        });

        _factory.ScopeProvider = _ => new AccessScopeResponse("test-user", [], false);

        var res = await _factory.CreateClient().GetAsync($"/graph/{doc}", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "403 を返すと『権限が無いだけで存在はする』ことが漏れる");
    }

    // 認証そのものが無い場合は 401（存在秘匿の前段）。
    [Fact]
    public async Task Returns_401_when_unauthenticated()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");

        var res = await client.GetAsync($"/graph/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
