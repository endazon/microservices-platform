using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using Microsoft.Extensions.DependencyInjection;
using RetrievalService.Api.Foundation.Ports;
using System.Net;
using System.Net.Http.Json;

namespace RetrievalService.Api.Tests;

// FR-03, UC-01: /search ハイブリッド検索のエンドポイント結合テスト（InMemory ストア）
public class HybridSearchEndpointTests
{
    private static ChunkPayload Chunk(string text, Dictionary<string, string>? attrs = null,
        List<string>? tags = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), $"doc:{text}", text,
            new float[1536], $"s3://bucket/{Guid.NewGuid()}.md", attrs ?? [], tags ?? []);

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

    // --- FR-04, FR-05, SC-01, SC-08, #540: 権限内属性値の照会（計画 ADR-0043）--------------

    private static async Task<AttributeValuesResponse> ListValuesAsync(
        TestWebApplicationFactory factory, string key, AccessScope? scope)
    {
        var resp = await factory.CreateClient()
            .PostAsJsonAsync("/search/attribute-values", new AttributeValuesRequest(key, scope));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<AttributeValuesResponse>())!;
    }

    // **ADR-0043 決定 1**: 返すのは「**到達できる文書に実際に付与された値**」だけである。
    // 権限外の文書にしか付かない値が混ざると、その存在が推測でき 404 原則が実質的に破れる。
    [Fact]
    public async Task AttributeValues_ReturnsOnlyValuesOnReachableDocuments()
    {
        await using var factory = new TestWebApplicationFactory();
        await SeedAsync(factory,
            Chunk("見える", new() { ["dept"] = "sales" }, ["公開資料"]),
            Chunk("見えない", new() { ["dept"] = "hr" }, ["極秘プロジェクト"]));

        var scope = new AccessScope([new AttributeFilter("dept", ["sales"])], GrantsAccess: true);
        var body = await ListValuesAsync(factory, AttributeValueKeys.Tags, scope);

        body.Values.Should().Equal(["公開資料"]);
        body.Values.Should().NotContain("極秘プロジェクト",
            "権限外の文書にしか付かない値は、その存在を推測させるため返さない");
    }

    // **ADR-0043 決定 2**: 件数を返さない。「12 件だが自分の検索では 8 件」＝**見えない文書が 4 件ある**。
    // **応答の JSON に件数らしきものが 1 つも無い**ことを、生の本文で見る
    // （DTO にフィールドが無いだけでなく、実装が余計なものを載せていないことまで確かめる）。
    [Fact]
    public async Task AttributeValues_ResponseCarriesNoCounts()
    {
        await using var factory = new TestWebApplicationFactory();
        await SeedAsync(factory,
            Chunk("A", tags: ["共有"]),
            Chunk("B", tags: ["共有"]),
            Chunk("C", tags: ["単独"]));

        var resp = await factory.CreateClient().PostAsJsonAsync("/search/attribute-values",
            new AttributeValuesRequest(AttributeValueKeys.Tags, new AccessScope([], GrantsAccess: true)));
        var raw = await resp.Content.ReadAsStringAsync();

        raw.Should().NotContainAny("count", "Count", "件数");
        // 「共有」は 2 件あるが、応答には値が 1 つ現れるだけで多重度も出ない。
        var body = await resp.Content.ReadFromJsonAsync<AttributeValuesResponse>();
        body!.Values.Should().Equal(["共有", "単独"]);
    }

    // **[[IADR-0151]] 決定 1**: ABAC フィルタは検索段と同じものを使う。
    // 同じスコープで検索した結果に現れる文書の値だけが候補になることを、両方引いて確かめる。
    [Fact]
    public async Task AttributeValues_UsesTheSameAbacFilterAsSearch()
    {
        await using var factory = new TestWebApplicationFactory();
        await SeedAsync(factory,
            Chunk("アルファ 社内", new() { ["confidentiality"] = "internal" }, ["社内"]),
            Chunk("アルファ 秘", new() { ["confidentiality"] = "confidential" }, ["秘"]));

        var scope = new AccessScope(
            [new AttributeFilter("confidentiality", ["internal"])], GrantsAccess: true);

        var searchResp = await factory.CreateClient().PostAsJsonAsync("/search",
            new SearchRequest("アルファ", TopK: 10, Scope: scope));
        var search = await searchResp.Content.ReadFromJsonAsync<SearchResponse>();
        var values = await ListValuesAsync(factory, AttributeValueKeys.Tags, scope);

        search!.Results.Should().ContainSingle("スコープが 1 件へ絞る");
        values.Values.Should().Equal(["社内"], "候補は検索に現れる集合と一致する");
    }

    // **[[IADR-0151]] 決定 5**: スコープ未解決・不許可は**空配列**（404 にも 403 にもしない）。
    // 候補が無いことと権限が無いことを利用者へ区別させない。
    [Theory]
    [InlineData(false)]  // GrantsAccess=false（deny-by-default）
    [InlineData(true)]   // Scope 自体が null
    public async Task AttributeValues_WhenScopeNotGranted_ReturnsEmpty(bool scopeIsNull)
    {
        await using var factory = new TestWebApplicationFactory();
        await SeedAsync(factory, Chunk("A", tags: ["社内"]));

        var scope = scopeIsNull ? null : new AccessScope([], GrantsAccess: false);
        var body = await ListValuesAsync(factory, AttributeValueKeys.Tags, scope);

        body.Values.Should().BeEmpty();
    }

    // `tags`（リスト項目）と `attributes.<key>`（ネスト項目・[[IADR-0014]]）の**両方**を引ける。
    [Fact]
    public async Task AttributeValues_ReadsBothTagsAndAbacAttributes()
    {
        await using var factory = new TestWebApplicationFactory();
        await SeedAsync(factory,
            Chunk("A", new() { ["department"] = "法務" }, ["規程"]),
            Chunk("B", new() { ["department"] = "経理" }, ["手順書"]));

        var scope = new AccessScope([], GrantsAccess: true);

        (await ListValuesAsync(factory, AttributeValueKeys.Tags, scope))
            .Values.Should().Equal(["手順書", "規程"]);
        (await ListValuesAsync(factory, "department", scope))
            .Values.Should().Equal(["法務", "経理"], "ネスト属性も同じ口から引ける");
    }

    // 未知・空のキーは空集合へ縮退する（呼び出し側の分岐を 1 か所に閉じる）。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-such-key")]
    public async Task AttributeValues_UnknownKey_ReturnsEmpty(string key)
    {
        await using var factory = new TestWebApplicationFactory();
        await SeedAsync(factory, Chunk("A", tags: ["社内"]));

        var body = await ListValuesAsync(factory, key, new AccessScope([], GrantsAccess: true));

        body.Values.Should().BeEmpty();
    }
}
