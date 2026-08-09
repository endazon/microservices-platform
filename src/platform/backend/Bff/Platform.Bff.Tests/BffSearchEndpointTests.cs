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

    // FR-03, SC-02, #532: 並び順は**利用者の指定をそのまま後段へ渡す**（BFF で正規化しない）。
    // 縮退（未知値 → 既定）は RetrievalService が一箇所で行う——2 か所で正規化すると規則が割れる。
    [Theory]
    [InlineData("updated")]
    [InlineData("relevance")]
    [InlineData("unknown-sort")]
    public async Task PostSearch_ForwardsSortByToDownstream(string sortBy)
    {
        factory.SearchScopeGranted = true;
        var resp = await factory.CreateClient()
            .PostAsJsonAsync("/bff/search", new { query = "経費", topK = 5, sortBy });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.LastSearchSortBy.Should().Be(sortBy, "BFF は縮退させずそのまま運ぶ");
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

    // --- FR-04, FR-05, SC-01, SC-08, #540: 権限内属性値の照会（計画 ADR-0043）--------------

    // 許可スコープがあれば後段の候補をそのまま返す（SC-01 / SC-08 の対象範囲フィルタの供給源）。
    [Fact]
    public async Task PostAttributeValues_WhenGranted_ReturnsCandidates()
    {
        factory.SearchScopeGranted = true;
        var resp = await factory.CreateClient()
            .PostAsJsonAsync("/bff/attribute-values", new { key = "tags" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<AttributeValuesResponse>();
        body!.Values.Should().Equal(["社内", "規程"]);
    }

    // **[[IADR-0151]] 決定 5**: スコープが解決できない（deny-by-default）ときは**空配列**。
    // **404 にも 403 にもしない** —— 候補が無いことと権限が無いことを利用者へ区別させない
    // （区別を与えると、試行錯誤で値の存在を探れてしまう）。
    [Fact]
    public async Task PostAttributeValues_WhenNotGranted_ReturnsEmpty_NotForbidden()
    {
        factory.SearchScopeGranted = false;
        var resp = await factory.CreateClient()
            .PostAsJsonAsync("/bff/attribute-values", new { key = "tags" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "403 / 404 で権限の有無を教えない");
        var body = await resp.Content.ReadFromJsonAsync<AttributeValuesResponse>();
        body!.Values.Should().BeEmpty();
    }

    // 権限昇格の防止: **クライアントが送った Scope は使わない。** サーバ側で解決した値で置き換える
    // （検索と同じ扱い）。後段へ渡した本文に、クライアントが送った偽の許可が現れないことを見る。
    [Fact]
    public async Task PostAttributeValues_IgnoresClientSuppliedScope()
    {
        // BffTestFactory は IClassFixture でテスト間に共有されるため、観測前に戻す。
        factory.LastAttributeValuesBody = null;
        factory.SearchScopeGranted = false;
        var resp = await factory.CreateClient().PostAsJsonAsync("/bff/attribute-values", new
        {
            key = "tags",
            scope = new { filters = Array.Empty<object>(), grantsAccess = true },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<AttributeValuesResponse>();
        body!.Values.Should().BeEmpty("サーバ側の解決が不許可なら、クライアント指定は無視される");
        factory.LastAttributeValuesBody.Should().BeNull("不許可なら後段を呼ばない");
    }

    // 空・空白のキーは後段を呼ばずに空配列で返す（無駄な往復をしない）。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PostAttributeValues_BlankKey_ReturnsEmptyWithoutCallingDownstream(string key)
    {
        factory.LastAttributeValuesBody = null;
        factory.SearchScopeGranted = true;
        var resp = await factory.CreateClient().PostAsJsonAsync("/bff/attribute-values", new { key });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<AttributeValuesResponse>())!.Values.Should().BeEmpty();
        factory.LastAttributeValuesBody.Should().BeNull();
    }
}
