using System.Net;
using AwesomeAssertions;
using GraphService.Domain;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Tests.Features.Graph.GetNode;

// FR-17, UC-10, ADR-0034 決定 2: **存在秘匿を固定する。**
//
// ADR-0034 は「利用者がリンク切れと権限不足を区別できないこと」を受け入れ済みの副作用として
// 明記している。区別できてしまうと、**権限外文書の存在そのものが漏れる**。
//
// 本テストは 3 つの経路（権限なし / 複製なし / 文書なし）が**応答として区別できない**ことを見る。
//
// 🔴 **対象は `/graph/{id}` と `/graph/{id}/neighbors` の 2 端点である**（#1248 追随）。
// クラス名は複数形だが、置き場は `GetNode/` のままにしてある（移動は差分を膨らませるだけで、
// 名前が指す範囲は本注記が持つ）。**neighbors 側を落としたのは移送 PR の見落としで、
// `Neighbors/Endpoint.cs` の注記だけが本クラスを指していた** —— 指し先が空だった。
[Trait("TestKind", "Integration")]
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
            // 属性の複製がまだ届いていないノード（IADR-0242 決定 12-3 で不可視）。
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

    // FR-17, UC-10, ADR-0034 決定 2 (#1248): **neighbors も同じ 3 経路で区別できない。**
    //
    // 🔴 `Neighbors/Endpoint.cs` の注記が本クラスを指していたが、**neighbors を触る試験は
    // 1 本も無かった**（GetNode の 3 本だけ）。指し先を実在させるために足す。
    // 近傍探索は起点の可視性判定が `GetNode` と別経路（`AuthorizedNode.Authorize` の前に
    // `GraphAccessResolver` の `Granted` を見る）なので、GetNode の 3 本では代替できない。
    [Fact]
    public async Task Neighbors_unauthorized_missing_and_nonexistent_are_indistinguishable()
    {
        var visible = Guid.NewGuid();
        var forbidden = Guid.NewGuid();
        var noAttributes = Guid.NewGuid();
        var nonexistent = Guid.NewGuid();

        await _factory.SeedAsync(db =>
        {
            db.Documents.Add(GraphDocument.Create(visible, "n-visible",
                new Dictionary<string, string> { ["confidentiality"] = "internal" },
                null, DateTimeOffset.UtcNow));
            db.Documents.Add(GraphDocument.Create(forbidden, "n-forbidden",
                new Dictionary<string, string> { ["confidentiality"] = "restricted" },
                null, DateTimeOffset.UtcNow));
            db.Documents.Add(GraphDocument.Create(noAttributes, "n-not-yet-synced",
                [], null, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        });

        _factory.ScopeProvider = _ => InternalOnly();
        var client = _factory.CreateClient();

        // 対照群: 見えるものは見える（「全部 404」で空振りしていないことの担保）。
        var ok = await client.GetAsync($"/graph/{visible}/neighbors", TestContext.Current.CancellationToken);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);

        var responses = new List<HttpResponseMessage>
        {
            await client.GetAsync($"/graph/{forbidden}/neighbors", TestContext.Current.CancellationToken),
            await client.GetAsync($"/graph/{noAttributes}/neighbors", TestContext.Current.CancellationToken),
            await client.GetAsync($"/graph/{nonexistent}/neighbors", TestContext.Current.CancellationToken),
        };

        foreach (var r in responses)
            r.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var bodies = new List<string>();
        foreach (var r in responses)
            bodies.Add(await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        bodies.Distinct().Should().HaveCount(1,
            "応答本文に差があると、そこから存在の有無が読める");

        responses.Select(r => r.Content.Headers.ContentType?.ToString() ?? "")
            .Distinct().Should().HaveCount(1);
    }

    // FR-17, UC-10, ADR-0034 決定 2・3 (#1248): **検証は認可より前**。権限の無いスコープでも
    // 400 が返る（hops / types の両方）。
    //
    // 🔴 これを認可の後ろへ動かすと、権限外・不存在は 404・可視の文書だけが 400 になり、
    // **hops=4 を投げるだけで文書の存在が判る。** 存在する文書を可視・不可視の対で置き、
    // **どちらも 400** であることで固定する（`GraphValidationResponseContractTests` の 2 本は
    // 不存在の ID しか見ていないので、可視／不可視の対はここが持つ）。
    [Fact]
    public async Task Neighbors_validation_runs_before_authorization_for_visible_and_hidden_alike()
    {
        var visible = Guid.NewGuid();
        var forbidden = Guid.NewGuid();

        await _factory.SeedAsync(db =>
        {
            db.Documents.Add(GraphDocument.Create(visible, "v-visible",
                new Dictionary<string, string> { ["confidentiality"] = "internal" },
                null, DateTimeOffset.UtcNow));
            db.Documents.Add(GraphDocument.Create(forbidden, "v-forbidden",
                new Dictionary<string, string> { ["confidentiality"] = "restricted" },
                null, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        });

        _factory.ScopeProvider = _ => InternalOnly();
        var client = _factory.CreateClient();

        // 対照群: 妥当な要求なら可視は 200・不可視は 404（＝この対は本当に見え方が違う）。
        (await client.GetAsync($"/graph/{visible}/neighbors", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/graph/{forbidden}/neighbors", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        var bad = $"?hops={GraphTraversal.MaxHops + 1}";
        foreach (var id in new[] { visible, forbidden })
        {
            var res = await client.GetAsync($"/graph/{id}/neighbors{bad}",
                TestContext.Current.CancellationToken);
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                "検証が認可より後ろだと不可視の文書だけ 404 になり、400/404 の差から存在が漏れる");
        }

        foreach (var id in new[] { visible, forbidden })
        {
            var res = await client.GetAsync($"/graph/{id}/neighbors?types=not-a-guid",
                TestContext.Current.CancellationToken);
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                "types の検証も認可より前である（hops と同じ理由）");
        }
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
