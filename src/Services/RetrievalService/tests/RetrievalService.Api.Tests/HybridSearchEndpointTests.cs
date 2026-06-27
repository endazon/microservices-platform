using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Dtos;
using Microsoft.Extensions.DependencyInjection;
using RetrievalService.Api.Abstractions;
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

        var resp = await factory.CreateClient()
            .PostAsJsonAsync("/search", new SearchRequest("アルファ", TopK: 10));

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

        var resp = await factory.CreateClient().PostAsJsonAsync("/search",
            new SearchRequest("四半期 売上", TopK: 10,
                AttributeFilters: new() { ["dept"] = "sales" }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>();
        var ids = body!.Results.Select(r => r.ChunkId).ToList();
        ids.Should().Contain(authorized.ChunkId);
        ids.Should().NotContain(forbidden.ChunkId, "権限の無い文書は結果に現れない");
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
