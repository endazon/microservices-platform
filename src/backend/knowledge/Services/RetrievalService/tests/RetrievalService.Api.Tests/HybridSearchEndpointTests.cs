using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Dtos;
using Microsoft.Extensions.DependencyInjection;
using RetrievalService.Api.Foundation.Ports;
using System.Net;
using System.Net.Http.Json;

namespace RetrievalService.Api.Tests;

// FR-03, UC-01: /search ハイブリッド検索のエンドポイント結合テスト（InMemory ストア）
public class HybridSearchEndpointTests
{
    private static ChunkPayload Chunk(string text, Dictionary<string, string>? attrs = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), $"doc:{text}", text,
            new float[1536], $"s3://bucket/{Guid.NewGuid()}.md", attrs ?? [], []);

    private static async Task SeedAsync(TestWebApplicationFactory factory, params ChunkPayload[] chunks)
    {
        var store = factory.Services.GetRequiredService<IVectorStore>();
        foreach (var c in chunks)
            await store.UpsertAsync(c);
    }

    // FR-03: キーワード一致文書が、ベクトル＋全文の融合で最上位になり、出典が付く
    [Fact]
    public async Task PostSearch_KeywordMatch_RanksTopAndHasCitation()
    {
        await using var factory = new TestWebApplicationFactory();
        var target = Chunk("アルファ 機能 の 説明");
        await SeedAsync(factory,
            target,
            Chunk("ベータ 別 の 概念"),
            Chunk("ガンマ 無関係 な 内容"));

        // FR-05: 検索には許可スコープが必須（fail-closed）。全件許可の空フィルタ＋GrantsAccess=true。
        var resp = await factory.CreateClient()
            .PostAsJsonAsync("/search", new SearchRequest("アルファ", TopK: 10,
                Scope: new AccessScope([], GrantsAccess: true)));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>();
        body!.Results.Should().NotBeEmpty();
        body.Results[0].ChunkId.Should().Be(target.ChunkId, "キーワード一致は両系統に出現し最上位になる");
        // 結果に出典が付く（受け入れ基準①）
        body.Results[0].DocumentTitle.Should().NotBeNullOrEmpty();
        body.Results[0].MarkdownUri.Should().NotBeNullOrEmpty();
    }

    // FR-03/FR-05: 権限の無い文書は検索結果に一切現れない（ABAC 属性フィルタ）
    [Fact]
    public async Task PostSearch_AppliesAbacFilter_ExcludesUnauthorized()
    {
        await using var factory = new TestWebApplicationFactory();
        var authorized = Chunk("四半期 売上 レポート", new() { ["dept"] = "sales" });
        var forbidden = Chunk("四半期 売上 機密", new() { ["dept"] = "hr" });
        await SeedAsync(factory, authorized, forbidden);

        // FR-05: 単値 AttributeFilters（FR-03 後方互換）は許可スコープと併用する（fail-closed）。
        var resp = await factory.CreateClient().PostAsJsonAsync("/search",
            new SearchRequest("四半期 売上", TopK: 10,
                AttributeFilters: new() { ["dept"] = "sales" },
                Scope: new AccessScope([], GrantsAccess: true)));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>();
        var ids = body!.Results.Select(r => r.ChunkId).ToList();
        ids.Should().Contain(authorized.ChunkId);
        ids.Should().NotContain(forbidden.ChunkId, "権限の無い文書は結果に現れない");
    }

    // FR-05: 多値 allow-list（confidentiality ∈ {public, internal}）で許可文書のみ返り、機密は除外される
    [Fact]
    public async Task PostSearch_MultiValueScope_ReturnsOnlyAllowedConfidentiality()
    {
        await using var factory = new TestWebApplicationFactory();
        var pub = Chunk("製品 概要 公開", new() { ["confidentiality"] = "public" });
        var intl = Chunk("製品 概要 社内", new() { ["confidentiality"] = "internal" });
        var conf = Chunk("製品 概要 機密", new() { ["confidentiality"] = "confidential" });
        await SeedAsync(factory, pub, intl, conf);

        var scope = new AccessScope(
            [new AttributeFilter("confidentiality", ["public", "internal"])], GrantsAccess: true);
        var resp = await factory.CreateClient().PostAsJsonAsync("/search",
            new SearchRequest("製品 概要", TopK: 10, Scope: scope));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>();
        var ids = body!.Results.Select(r => r.ChunkId).ToList();
        ids.Should().Contain([pub.ChunkId, intl.ChunkId]);
        ids.Should().NotContain(conf.ChunkId, "許可値集合に無い機密文書は現れない");
    }

    // FR-05: スコープ属性キーを持たない文書は除外される（deny-by-default の徹底）
    [Fact]
    public async Task PostSearch_DocumentMissingScopedAttribute_IsExcluded()
    {
        await using var factory = new TestWebApplicationFactory();
        var tagged = Chunk("規程 文書 タグ付き", new() { ["confidentiality"] = "internal" });
        var untagged = Chunk("規程 文書 タグ無し");
        await SeedAsync(factory, tagged, untagged);

        var scope = new AccessScope(
            [new AttributeFilter("confidentiality", ["internal"])], GrantsAccess: true);
        var resp = await factory.CreateClient().PostAsJsonAsync("/search",
            new SearchRequest("規程 文書", TopK: 10, Scope: scope));

        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>();
        var ids = body!.Results.Select(r => r.ChunkId).ToList();
        ids.Should().Contain(tagged.ChunkId);
        ids.Should().NotContain(untagged.ChunkId, "属性キーを持たない文書は除外される");
    }

    // FR-05: 許可ポリシー無し（GrantsAccess=false）の利用者には何も返さない（deny-by-default）
    [Fact]
    public async Task PostSearch_ScopeDeniesAccess_ReturnsEmpty()
    {
        await using var factory = new TestWebApplicationFactory();
        await SeedAsync(factory,
            Chunk("公開 文書", new() { ["confidentiality"] = "public" }));

        var deniedScope = new AccessScope([], GrantsAccess: false);
        var resp = await factory.CreateClient().PostAsJsonAsync("/search",
            new SearchRequest("公開 文書", TopK: 10, Scope: deniedScope));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>();
        body!.Results.Should().BeEmpty("許可ポリシーが無い利用者は何も閲覧できない");
    }

    // FR-05, IADR-0012: Scope 未指定（null）は許可ポリシー未解決とみなし何も返さない（fail-closed）。
    //   受け入れ基準「Scope 未指定 → 結果 0 件」。呼び出し側 Scope を無検証で信任し、
    //   フィルタ無しで全件返却する ABAC 全面バイパスを塞ぐ。
    [Fact]
    public async Task PostSearch_ScopeUnspecified_ReturnsEmpty()
    {
        await using var factory = new TestWebApplicationFactory();
        // クエリに一致する文書が存在しても、Scope が無ければ 1 件も返さない。
        await SeedAsync(factory,
            Chunk("公開 資料", new() { ["confidentiality"] = "public" }),
            Chunk("公開 手順 書"));

        var resp = await factory.CreateClient()
            .PostAsJsonAsync("/search", new SearchRequest("公開", TopK: 10));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>();
        body!.Results.Should().BeEmpty("Scope 未指定は deny-by-default（fail-closed）で 0 件");
    }

    // 空クエリは空結果（防御）
    [Fact]
    public async Task PostSearch_EmptyQuery_ReturnsEmpty()
    {
        await using var factory = new TestWebApplicationFactory();
        await SeedAsync(factory, Chunk("何らかの 文書"));

        var resp = await factory.CreateClient()
            .PostAsJsonAsync("/search", new SearchRequest("", TopK: 5));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>();
        body!.Results.Should().BeEmpty();
    }
}
