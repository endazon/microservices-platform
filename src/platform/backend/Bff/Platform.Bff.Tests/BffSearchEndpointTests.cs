using AwesomeAssertions;
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
        var resp = await factory.CreateClient().PostAsJsonAsync("/bff/search", new { query = "経費", topK = 5 }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>(TestContext.Current.CancellationToken);
        body!.Results.Should().ContainSingle(r => r.DocumentTitle == "経費規程 2025");
    }

    // FR-03, SC-02, #536: 後段が返す更新日時を**欠落させずに透過**する（IADR-0149）。
    // BFF は SearchResponse で型付けして中継するだけなので実装は変わらないが、契約のメンバーが
    // 増えたときに落ちる場所が無いと静かに欠ける（SC-06 の `nextSyncAt` / 同期健全性と同じ守り）。
    [Fact]
    public async Task PostSearch_PassesThroughUpdatedAt()
    {
        factory.SearchScopeGranted = true;
        var resp = await factory.CreateClient().PostAsJsonAsync("/bff/search", new { query = "経費", topK = 5 }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>(TestContext.Current.CancellationToken);
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
            .PostAsJsonAsync("/bff/search", new { query = "経費", topK = 5, sortBy }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.LastSearchSortBy.Should().Be(sortBy, "BFF は縮退させずそのまま運ぶ");
    }

    // FR-05, FR-17, ADR-0034, ADR-0035 (#970): 🔴 利用者の Authorization を**そのまま**後段へ伝播する
    // （方式 A）。二段検索の段はこのヘッダを RetrievalService → GraphService と運んでホップごと
    // ABAC を効かせる —— 伝播しないと、段を有効化しても展開は常に 0 件だった（IADR-0263 残件 2）。
    [Fact]
    public async Task PostSearch_ForwardsTheCallersAuthorizationToRetrieval()
    {
        factory.SearchScopeGranted = true;
        factory.LastSearchForwardedAuthorization = null;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer user-jwt");

        var resp = await client.PostAsJsonAsync("/bff/search", new { query = "経費", topK = 5 },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.LastSearchForwardedAuthorization.Should().Be("Bearer user-jwt",
            "本文で scope を渡す方式 B ではなく、ヘッダをそのまま伝播する（方式 A）");
    }

    // 同（否定形）: 受信リクエストにヘッダが無ければ付けない（BFF がトークンを捏造しない。
    // 縮退の判断とその警告は RetrievalService 側が一元で持つ）。
    [Fact]
    public async Task PostSearch_DoesNotInventAnAuthorizationHeader()
    {
        factory.SearchScopeGranted = true;
        factory.LastSearchForwardedAuthorization = "sentinel";

        var resp = await factory.CreateClient().PostAsJsonAsync("/bff/search",
            new { query = "経費", topK = 5 }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.LastSearchForwardedAuthorization.Should().BeNull("無いヘッダは無いまま運ぶ");
    }

    [Fact]
    public async Task PostSearch_WhenNotGranted_ReturnsEmpty_DenyByDefault()
    {
        factory.SearchScopeGranted = false;
        var resp = await factory.CreateClient().PostAsJsonAsync("/bff/search", new { query = "経費", topK = 5 }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>(TestContext.Current.CancellationToken);
        body!.Results.Should().BeEmpty();          // 権限外は空（存在秘匿）
        body.TotalHits.Should().Be(0);
    }

    [Fact]
    public async Task PostSearch_EmptyQuery_ReturnsEmptyWithoutResolving()
    {
        var resp = await factory.CreateClient().PostAsJsonAsync("/bff/search", new { query = "  " }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>(TestContext.Current.CancellationToken);
        body!.Results.Should().BeEmpty();
    }

    // 権限昇格の防止: クライアントが Scope(GrantsAccess=true) を送っても、サーバ側解決が不許可なら空。
    [Fact]
    public async Task PostSearch_IgnoresClientSuppliedScope()
    {
        factory.SearchScopeGranted = false;
        var forgedScope = new AccessScope([], GrantsAccess: true);
        var resp = await factory.CreateClient()
            .PostAsJsonAsync("/bff/search", new { query = "経費", topK = 5, scope = forgedScope }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>(TestContext.Current.CancellationToken);
        body!.Results.Should().BeEmpty();          // クライアント Scope は無視される
    }

    // --- FR-04, FR-05, SC-01, SC-08, #540: 権限内属性値の照会（計画 ADR-0043）--------------

    // 許可スコープがあれば後段の候補をそのまま返す（SC-01 / SC-08 の対象範囲フィルタの供給源）。
    [Fact]
    public async Task PostAttributeValues_WhenGranted_ReturnsCandidates()
    {
        factory.SearchScopeGranted = true;
        var resp = await factory.CreateClient()
            .PostAsJsonAsync("/bff/attribute-values", new { key = "tags" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<AttributeValuesResponse>(TestContext.Current.CancellationToken);
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
            .PostAsJsonAsync("/bff/attribute-values", new { key = "tags" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "403 / 404 で権限の有無を教えない");
        var body = await resp.Content.ReadFromJsonAsync<AttributeValuesResponse>(TestContext.Current.CancellationToken);
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
        }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<AttributeValuesResponse>(TestContext.Current.CancellationToken);
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
        var resp = await factory.CreateClient().PostAsJsonAsync("/bff/attribute-values", new { key }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<AttributeValuesResponse>(TestContext.Current.CancellationToken))!.Values.Should().BeEmpty();
        factory.LastAttributeValuesBody.Should().BeNull();
    }

    // FR-09, SC-05, SC-09, #634: 管理者・運用者には**辞書そのもの**が添う（IADR-0152 決定 3）。
    // **口は 1 系統のまま**（ADR-0043 決定 4）——新しい読み取り口を作っていない。
    [Theory]
    [InlineData("platform-admin")]
    [InlineData("platform-operator")]
    public async Task PostAttributeValues_DictionaryReader_GetsDictionary(string role)
    {
        factory.SearchScopeGranted = true;
        factory.TagDictionaryFetched = false;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, role);

        var resp = await client.PostAsJsonAsync("/bff/attribute-values", new { key = "tags" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await resp.Content.ReadFromJsonAsync<AttributeValuesResponse>(TestContext.Current.CancellationToken))!;
        body.Dictionary.Should().NotBeNull();
        body.Dictionary!.Tags.Should().NotBeEmpty();
        factory.TagDictionaryFetched.Should().BeTrue();
    }

    // **一般利用者には辞書が出ない。** 辞書を丸ごと返すと権限外の文書に固有の値から
    // その存在が推測できる（ADR-0043 決定 1 / IADR-0152 決定 4）。
    // **後段を呼んでもいない**ことまで固定する（呼べば後段が 403 で止めるが、呼ぶこと自体が無駄である）。
    [Fact]
    public async Task PostAttributeValues_GeneralUser_GetsNoDictionary_AndDownstreamNotCalled()
    {
        factory.SearchScopeGranted = true;
        factory.TagDictionaryFetched = false;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "viewer");

        var resp = await client.PostAsJsonAsync("/bff/attribute-values", new { key = "tags" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await resp.Content.ReadFromJsonAsync<AttributeValuesResponse>(TestContext.Current.CancellationToken))!;
        body.Dictionary.Should().BeNull("一般利用者の応答形は #540 から変わらない");
        body.Values.Should().NotBeNull();
        factory.TagDictionaryFetched.Should().BeFalse();
    }

    // 辞書が在るのは `tags` だけである。ABAC 属性の許可値は `AttributeDefinition.AllowedValues` が
    // 持っており、**タグ辞書ではない**（計画も同じ切り分けをしている）。
    [Fact]
    public async Task PostAttributeValues_NonTagKey_GetsNoDictionary()
    {
        factory.SearchScopeGranted = true;
        factory.TagDictionaryFetched = false;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-admin");

        var resp = await client.PostAsJsonAsync("/bff/attribute-values", new { key = "department" }, TestContext.Current.CancellationToken);

        (await resp.Content.ReadFromJsonAsync<AttributeValuesResponse>(TestContext.Current.CancellationToken))!.Dictionary.Should().BeNull();
        factory.TagDictionaryFetched.Should().BeFalse();
    }

    // FR-04, FR-05, SC-01, SC-08, #540: **後段が返した非 2xx はそのまま透過する。**
    // **縮退（200 空配列）で潰さない** —— 縮退が守るのは「権限外の存在を示さない」ことであって、
    // **後段の障害を利用者から隠すことではない**。潰すと運用側が不調に気づけない
    // （`docs/api/BFF_bff-surface.md` の縮退表の注記・[[IADR-0151]] 決定 5 の射程）。
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task PostAttributeValues_WhenDownstreamFails_PropagatesStatus_NotEmptyList(HttpStatusCode status)
    {
        factory.SearchScopeGranted = true;
        factory.AttributeValuesStatusCode = status;
        try
        {
            var resp = await factory.CreateClient()
                .PostAsJsonAsync("/bff/attribute-values", new { key = "tags" }, TestContext.Current.CancellationToken);

            resp.StatusCode.Should().Be(status, "後段の非 2xx は透過する（空配列へ畳まない）");
        }
        finally
        {
            // BffTestFactory は IClassFixture で共有される。後続テストへ漏らさない。
            factory.AttributeValuesStatusCode = HttpStatusCode.OK;
        }
    }
}
