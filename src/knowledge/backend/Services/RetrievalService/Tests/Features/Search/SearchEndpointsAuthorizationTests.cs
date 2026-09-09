using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Domain.Ports;

namespace RetrievalService.Tests.Features.Search;

// FR-03, FR-04, FR-05, NFR-09, UC-01, SC-01, SC-08, ADR-0004, ADR-0032, ADR-0084,
// [[IADR-0009]], [[IADR-0044]], [[IADR-0151]] 決定 5, [[IADR-0416]] 決定 5,
// [[IADR-0417]] 決定 4, [[IADR-0418]] (#1318 欠陥 B):
// 🔴 **`/search` 群は認証を要する。**
//
// [[IADR-0416]] は「呼び出し元の主張を信じない」形へ変えて悪用可能性を塞いだが、
// **認可そのものは掛けなかった**（決定 5 で明示的に留保）。本ファイルはその門を固定する。
//
// 🔴 **3 段で測る。1 つでも欠けると門は測れていない。**
//   ① 未認証は 401（本文に文書を含まない）
//   ② 認証済みだが**権限が無い**主体には **200 ＋ 空**（存在秘匿は保たれる。陰性対照）
//   ③ 認証済み・許可では従来どおり結果が返る（陽性対照）
// ②が無いと「401 と 200 空の区別が消えた」ことに気づけず、③が無いと
// **「常に空を返す実装」が①②を通してしまう。**
//
// 🔴 ④ **構造の門**（配線そのもの）も置く。①は「401 になる」ことしか測らず、
// 器の都合で通る余地が残る。終端の認可メタデータを読み、**陰性対照として
// `/health/live` が認可を持たないこと**を同じ試験で主張する
// （全端点が認可を持つ実装なら④は無意味に緑になる）。
[Trait("TestKind", "Integration")]
public class SearchEndpointsAuthorizationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Term = "四半期 売上 レポート";

    private static ChunkPayload Chunk(string text) =>
        new(Guid.NewGuid(), Guid.NewGuid(), $"doc:{text}", text,
            new float[1536], $"s3://bucket/{Guid.NewGuid()}.md",
            new Dictionary<string, string> { ["dept"] = "sales" }, []);

    private static async Task<ChunkPayload> SeedAsync(TestWebApplicationFactory factory)
    {
        var chunk = Chunk(Term);
        await factory.Services.GetRequiredService<IVectorStore>().UpsertAsync(chunk);
        return chunk;
    }

    private static SearchRequest Search() =>
        new(Term, TopK: 10, Scope: new AccessScope([], GrantsAccess: true));

    private static AttributeValuesRequest Values() =>
        new("dept", new AccessScope([], GrantsAccess: true));

    // ── ① 未認証は 401 ────────────────────────────────────────────

    // FR-05, NFR-09, ADR-0084: 🔴 **未認証の検索は 401 である。**
    // 本文には文書が 1 バイトも出ない —— 401 の本文で漏らしては門の意味が無い。
    [Fact]
    public async Task Unauthenticated_search_is_rejected_and_leaks_no_document()
    {
        await using var factory = new TestWebApplicationFactory();
        var seeded = await SeedAsync(factory);

        var resp = await factory.CreateAnonymousClient()
            .PostAsJsonAsync("/search", Search(), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await resp.Content.ReadAsStringAsync(Ct);
        body.Should().NotContain(seeded.ChunkId.ToString(), "401 の本文で文書を漏らさない");
        body.Should().NotContain(Term);
    }

    // FR-04, FR-05, NFR-09, ADR-0084: 🔴 **属性値照会にも同じ門が要る。**
    // 片面だけ掛けると「検索は塞いだが候補一覧から権限外の値が読める」形が残る。
    [Fact]
    public async Task Unauthenticated_attribute_values_is_rejected_and_leaks_no_value()
    {
        await using var factory = new TestWebApplicationFactory();
        await SeedAsync(factory);

        var resp = await factory.CreateAnonymousClient()
            .PostAsJsonAsync("/search/attribute-values", Values(), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await resp.Content.ReadAsStringAsync(Ct)).Should().NotContain("sales");
    }

    // ── ② 認証済み・権限なしは 200 ＋ 空（存在秘匿。陰性対照） ──────

    // FR-05, NFR-09, SC-01, [[IADR-0009]], [[IADR-0151]] 決定 5:
    // 🔴 **門を掛けても存在秘匿は崩さない。** 認証済みで解決器が deny を返す主体には
    // 403 でも 404 でもなく **200 ＋ 空**を返す —— 「権限が無い」と「候補が無い」を
    // 区別させない現行の契約は、認証の門の**内側**にそのまま残る。
    [Fact]
    public async Task Authenticated_but_denied_search_returns_empty_not_a_rejection()
    {
        await using var factory = new TestWebApplicationFactory();
        factory.Authoritative = new AccessScopeResponse("u1", [], Granted: false);
        await SeedAsync(factory);

        var resp = await factory.CreateClient().PostAsJsonAsync("/search", Search(), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "403 / 404 にすると権限の有無が読める");
        (await resp.Content.ReadFromJsonAsync<SearchResponse>(Ct))!
            .Results.Should().BeEmpty();
    }

    // FR-04, FR-05, SC-08, [[IADR-0151]] 決定 5: 属性値照会も同じ（200 ＋ 空配列）。
    [Fact]
    public async Task Authenticated_but_denied_attribute_values_returns_empty_not_a_rejection()
    {
        await using var factory = new TestWebApplicationFactory();
        factory.Authoritative = new AccessScopeResponse("u1", [], Granted: false);
        await SeedAsync(factory);

        var resp = await factory.CreateClient()
            .PostAsJsonAsync("/search/attribute-values", Values(), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<AttributeValuesResponse>(Ct))!
            .Values.Should().BeEmpty();
    }

    // ── ③ 認証済み・許可は従来どおり（陽性対照） ────────────────────

    // FR-03, UC-01, NFR-09: ★ **陽性対照。** これが無いと「常に空を返す実装」が①②を通す。
    [Fact]
    public async Task Authenticated_and_allowed_search_still_returns_results()
    {
        await using var factory = new TestWebApplicationFactory();
        var seeded = await SeedAsync(factory);

        var resp = await factory.CreateClient().PostAsJsonAsync("/search", Search(), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<SearchResponse>(Ct))!
            .Results.Select(r => r.ChunkId).Should().Contain(seeded.ChunkId,
                "★ 陽性対照 —— 門を掛けても認証済み・許可の経路は変わらない");
    }

    // FR-04, SC-08, NFR-09: ★ 陽性対照（属性値照会）。
    [Fact]
    public async Task Authenticated_and_allowed_attribute_values_still_returns_values()
    {
        await using var factory = new TestWebApplicationFactory();
        await SeedAsync(factory);

        var resp = await factory.CreateClient()
            .PostAsJsonAsync("/search/attribute-values", Values(), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<AttributeValuesResponse>(Ct))!
            .Values.Should().Contain("sales", "★ 陽性対照");
    }

    // ── ④ 構造の門（配線そのもの） ──────────────────────────────

    // FR-05, NFR-09, ADR-0084, [[IADR-0418]]: 🔴 **`/search` 群の終端が認可メタデータを持つ。**
    //
    // 状態コードの試験（①）は器の都合で通り得るので、**実 `Program.cs` の配線を読む門**を置く。
    // 🔴 **陰性対照つき** —— `/health/live` は認可を持たない。これが無いと
    // 「全端点に認可を積む実装」でも本試験は緑になり、何も固定できていない。
    [Fact]
    public async Task The_search_group_carries_authorization_metadata()
    {
        await using var factory = new TestWebApplicationFactory();
        factory.CreateClient().Dispose();   // ホストを起こす（配線はここで確定する）

        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => (
                Pattern: "/" + (e.RoutePattern.RawText ?? string.Empty).Trim('/'),
                HasAuthz: e.Metadata.GetMetadata<IAuthorizeData>() is not null))
            .ToList();

        var search = routes.Where(r => r.Pattern == "/search"
            || r.Pattern.StartsWith("/search/", StringComparison.Ordinal)).ToList();

        // 陽性対照: 群の終端は 2 つある（検索本体と属性値照会）。0 件の一覧に対して
        // `OnlyContain` は空虚に真になるため、**先に件数を主張する。**
        search.Should().HaveCount(2, "`/search` と `/search/attribute-values`");
        search.Should().OnlyContain(r => r.HasAuthz,
            "#1318 欠陥 B —— 群に RequireAuthorization() が掛かっていること");

        // 🔴 陰性対照: 群の外は触っていない（`/health/live` は無認可のままである）。
        routes.Should().Contain(r => r.Pattern == "/health/live" && !r.HasAuthz,
            "全端点へ認可を積んだのではない —— 掛けたのは /search 群だけである");
    }
}
