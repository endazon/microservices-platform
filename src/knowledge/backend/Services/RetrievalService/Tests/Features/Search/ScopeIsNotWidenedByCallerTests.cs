using System.Net.Http.Json;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using Microsoft.Extensions.DependencyInjection;
using RetrievalService.Domain.Ports;

namespace RetrievalService.Tests.Features.Search;

// FR-03, FR-05, NFR-09, UC-01, SC-01, ADR-0004, ADR-0043, [[IADR-0012]], [[IADR-0151]],
// [[IADR-0253]] 決定 2, [[IADR-0410]] (#1339):
// 🔴 **呼び出し元が送る値で ABAC の許可が広がらないこと。**
//
// `HybridSearchService.BuildFilters` は分岐の無い経路で、利用者指定の単値フィルタと
// ABAC スコープのフィルタを**同じキーの下で結合**している。結合が union であれば、
// 利用者が指定したキーの値が**許可値集合へ足される** —— 絞り込みのつもりの入力が
// **権限を広げる**ことになる。
//
// 本ファイルはその「広がらなさ」を実測で固定する。
[Trait("TestKind", "Integration")]
public class ScopeIsNotWidenedByCallerTests
{
    private static ChunkPayload Chunk(string text, Dictionary<string, string> attrs) =>
        new(Guid.NewGuid(), Guid.NewGuid(), $"doc:{text}", text,
            new float[1536], $"s3://bucket/{Guid.NewGuid()}.md", attrs, []);

    private static async Task SeedAsync(TestWebApplicationFactory factory, params ChunkPayload[] chunks)
    {
        var store = factory.Services.GetRequiredService<IVectorStore>();
        foreach (var c in chunks)
            await store.UpsertAsync(c);
    }

    // 🔴 **利用者指定フィルタは ABAC の許可値集合を広げない。**
    //
    // 許可は `dept ∈ {sales}` だけである。利用者が `dept=hr` を指定しても、
    // **hr の文書が現れてはならない**（指定は絞り込みであって権限ではない）。
    [Fact]
    public async Task A_caller_supplied_filter_cannot_add_values_to_the_allow_list()
    {
        await using var factory = new TestWebApplicationFactory();
        var allowed = Chunk("四半期 売上 レポート", new() { ["dept"] = "sales" });
        var forbidden = Chunk("四半期 売上 機密", new() { ["dept"] = "hr" });
        await SeedAsync(factory, allowed, forbidden);

        var resp = await factory.CreateClient().PostAsJsonAsync("/search",
            new SearchRequest("四半期 売上", TopK: 10,
                AttributeFilters: new() { ["dept"] = "hr" },
                Scope: new AccessScope(
                    [new AttributeFilter("dept", ["sales"])], GrantsAccess: true)),
            TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>(TestContext.Current.CancellationToken);
        var ids = body!.Results.Select(r => r.ChunkId).ToList();

        ids.Should().NotContain(forbidden.ChunkId,
            "利用者が指定したキーの値が許可値集合へ足されると、指定するだけで権限が広がる");
    }

    // 陽性対照: 許可の**中で**絞る指定は効く（絞り込みとしては機能している）。
    // これが無いと、上の主張は「指定を丸ごと無視する実装」でも緑になる。
    [Fact]
    public async Task A_caller_supplied_filter_still_narrows_within_the_allow_list()
    {
        await using var factory = new TestWebApplicationFactory();
        var sales = Chunk("四半期 売上 レポート", new() { ["dept"] = "sales" });
        var eng = Chunk("四半期 売上 開発", new() { ["dept"] = "eng" });
        await SeedAsync(factory, sales, eng);

        var resp = await factory.CreateClient().PostAsJsonAsync("/search",
            new SearchRequest("四半期 売上", TopK: 10,
                AttributeFilters: new() { ["dept"] = "sales" },
                Scope: new AccessScope(
                    [new AttributeFilter("dept", ["sales", "eng"])], GrantsAccess: true)),
            TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>(TestContext.Current.CancellationToken);
        var ids = body!.Results.Select(r => r.ChunkId).ToList();

        ids.Should().Contain(sales.ChunkId, "★ 陽性対照 —— 許可の中の指定は結果に残る");
        ids.Should().NotContain(eng.ChunkId, "指定したキーの外は絞り込まれる");
    }

    // ── FR-05, NFR-09, ADR-0034 決定 1, [[IADR-0410]], [[IADR-0416]] (#1339) ──
    // 🔴 **権限の根拠は受け口が自分で引く。呼び出し元の `Scope` は絞り込みでしかない。**

    // 🔴 T-1339-a（本 issue の核心）: **偽の `Scope` は効かない。**
    // 呼び出し元が「全部見てよい」と主張しても、自分で引いた許可の外は返らない。
    [Fact]
    public async Task A_forged_scope_cannot_reach_beyond_what_the_service_resolves()
    {
        await using var factory = new TestWebApplicationFactory();
        // 🔴 受け口が自分で引く許可は sales だけである。
        factory.Authoritative = new AccessScopeResponse(
            "u1", [new AttributeFilter("dept", ["sales"])], Granted: true);

        var sales = Chunk("四半期 売上 レポート", new() { ["dept"] = "sales" });
        var hr = Chunk("四半期 売上 人事", new() { ["dept"] = "hr" });
        await SeedAsync(factory, sales, hr);

        // 呼び出し元は「制約なしで全部見てよい」と主張する。
        var resp = await factory.CreateClient().PostAsJsonAsync("/search",
            new SearchRequest("四半期 売上", TopK: 10,
                Scope: new AccessScope([], GrantsAccess: true)),
            TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>(TestContext.Current.CancellationToken);
        var ids = body!.Results.Select(r => r.ChunkId).ToList();

        ids.Should().NotContain(hr.ChunkId, "主張は権限の根拠ではない");
        ids.Should().Contain(sales.ChunkId, "★ 陽性対照 —— 自分で引いた許可の中は返る");
    }

    // 🔴 T-1339-b: **正直な呼び出し元の結果は変わらない**（絞り込みとしては効き続ける）。
    [Fact]
    public async Task An_honest_scope_still_narrows_as_before()
    {
        await using var factory = new TestWebApplicationFactory();
        factory.Authoritative = new AccessScopeResponse(
            "u1", [new AttributeFilter("dept", ["sales", "eng"])], Granted: true);

        var sales = Chunk("四半期 売上 レポート", new() { ["dept"] = "sales" });
        var eng = Chunk("四半期 売上 開発", new() { ["dept"] = "eng" });
        await SeedAsync(factory, sales, eng);

        // 呼び出し元は自分の許可の中で sales へ絞る（AiAnalysis のデータ範囲がこの形）。
        var resp = await factory.CreateClient().PostAsJsonAsync("/search",
            new SearchRequest("四半期 売上", TopK: 10,
                Scope: new AccessScope([new AttributeFilter("dept", ["sales"])], GrantsAccess: true)),
            TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>(TestContext.Current.CancellationToken);
        var ids = body!.Results.Select(r => r.ChunkId).ToList();

        ids.Should().Contain(sales.ChunkId);
        ids.Should().NotContain(eng.ChunkId, "絞り込みとしては従来どおり効く");
    }

    // 🔴 T-1339-c: **自分で引けなければ空**（未認証・認可サービス不調のいずれも）。
    [Fact]
    public async Task Nothing_is_returned_when_the_service_cannot_resolve_a_scope()
    {
        await using var factory = new TestWebApplicationFactory();
        factory.Authoritative = new AccessScopeResponse("anonymous", [], Granted: false);

        var doc = Chunk("四半期 売上 レポート", new() { ["dept"] = "sales" });
        await SeedAsync(factory, doc);

        var resp = await factory.CreateClient().PostAsJsonAsync("/search",
            new SearchRequest("四半期 売上", TopK: 10,
                Scope: new AccessScope([], GrantsAccess: true)),
            TestContext.Current.CancellationToken);

        (await resp.Content.ReadFromJsonAsync<SearchResponse>(TestContext.Current.CancellationToken))!
            .Results.Should().BeEmpty("引けなければ deny（fail-closed）");
    }

    // 🔴 T-1339-d: **属性値照会にも同じ統制が要る。**
    [Fact]
    public async Task The_attribute_values_endpoint_is_governed_by_the_same_control()
    {
        await using var factory = new TestWebApplicationFactory();
        factory.Authoritative = new AccessScopeResponse(
            "u1", [new AttributeFilter("dept", ["sales"])], Granted: true);

        await SeedAsync(factory,
            Chunk("売上", new() { ["dept"] = "sales" }),
            Chunk("人事", new() { ["dept"] = "hr" }));

        var resp = await factory.CreateClient().PostAsJsonAsync("/search/attribute-values",
            new AttributeValuesRequest("dept", new AccessScope([], GrantsAccess: true)),
            TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadFromJsonAsync<AttributeValuesResponse>(
            TestContext.Current.CancellationToken);

        body!.Values.Should().NotContain("hr", "主張で候補が広がってはならない");
        body.Values.Should().Contain("sales", "★ 陽性対照 —— 許可の中の値は候補に出る");
    }
}
