using FluentAssertions;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using System.Net;
using System.Net.Http.Json;

namespace Platform.Bff.Tests;

// FR-03, FR-05, UC-01, SC-01: /bff/search が ABAC スコープ解決 → 検索を集約し、deny-by-default で
// 権限外は空を返すこと、クライアント指定 Scope を信頼しないことを検証する。
public class BffSearchEndpointTests(BffTestFactory factory) : IClassFixture<BffTestFactory>
{
    [Fact]
    public async Task PostSearch_WhenGranted_ReturnsAggregatedResults()
    {
        factory.SearchScopeGranted = true;
        var resp = await factory.CreateClient().PostAsJsonAsync("/bff/search", new { query = "経費", topK = 5 });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>();
        body!.Results.Should().ContainSingle(r => r.DocumentTitle == "経費規程 2025");
    }

    // FR-03, SC-02, #536: 後段が返す更新日時を**欠落させずに透過**する（IADR-0149）。
    // BFF は SearchResponse で型付けして中継するだけなので実装は変わらないが、契約のメンバーが
    // 増えたときに落ちる場所が無いと静かに欠ける（SC-06 の `nextSyncAt` / 同期健全性と同じ守り）。
    [Fact]
    public async Task PostSearch_PassesThroughUpdatedAt()
    {
        factory.SearchScopeGranted = true;
        var resp = await factory.CreateClient().PostAsJsonAsync("/bff/search", new { query = "経費", topK = 5 });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>();
        body!.Results.Should().OnlyContain(r => r.UpdatedAt == BffTestFactory.StubSearchUpdatedAt,
            "後段が返す更新日時をそのまま運ぶ（BFF で時刻を作らない）");
    }

    [Fact]
    public async Task PostSearch_WhenNotGranted_ReturnsEmpty_DenyByDefault()
    {
        factory.SearchScopeGranted = false;
        var resp = await factory.CreateClient().PostAsJsonAsync("/bff/search", new { query = "経費", topK = 5 });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>();
        body!.Results.Should().BeEmpty();          // 権限外は空（存在秘匿）
        body.TotalHits.Should().Be(0);
    }

    [Fact]
    public async Task PostSearch_EmptyQuery_ReturnsEmptyWithoutResolving()
    {
        var resp = await factory.CreateClient().PostAsJsonAsync("/bff/search", new { query = "  " });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>();
        body!.Results.Should().BeEmpty();
    }

    // 権限昇格の防止: クライアントが Scope(GrantsAccess=true) を送っても、サーバ側解決が不許可なら空。
    [Fact]
    public async Task PostSearch_IgnoresClientSuppliedScope()
    {
        factory.SearchScopeGranted = false;
        var forgedScope = new AccessScope([], GrantsAccess: true);
        var resp = await factory.CreateClient()
            .PostAsJsonAsync("/bff/search", new { query = "経費", topK = 5, scope = forgedScope });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>();
        body!.Results.Should().BeEmpty();          // クライアント Scope は無視される
    }
}
