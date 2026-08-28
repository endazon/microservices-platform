using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GraphService.Domain;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Tests;

// FR-17, ADR-0034 決定 8, IADR-0242 決定 6: 利用者が明示的に辺を付与する API（#913）。
//
// 🔴 **本クラスの中心は「辺を張る行為が権限外文書の存在を確かめる手段にならない」ことである。**
// 検証しなければ、張れたら在る・張れなければ無い、という探索経路になる。
public class UserAuthoredEdgeTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public UserAuthoredEdgeTests(TestWebApplicationFactory factory) => _factory = factory;

    private static AccessScopeResponse InternalOnly()
        => new("test-user", [new AttributeFilter("confidentiality", ["internal"])], true);

    private static GraphDocument Node(Guid id, string confidentiality)
        => GraphDocument.Create(id, "d",
            new Dictionary<string, string> { ["confidentiality"] = confidentiality },
            null, DateTimeOffset.UtcNow);

    private async Task<(Guid Visible, Guid Forbidden, Guid TypeId)> SeedAsync()
    {
        var visible = Guid.NewGuid();
        var visible2 = Guid.NewGuid();
        var forbidden = Guid.NewGuid();
        var type = EdgeType.Create($"e-{Guid.NewGuid():N}", EdgeTypeLayer.Core, false);
        await _factory.SeedAsync(db =>
        {
            db.EdgeTypes.Add(type);
            db.Documents.Add(Node(visible, "internal"));
            db.Documents.Add(Node(visible2, "internal"));
            db.Documents.Add(Node(forbidden, "restricted"));
            return Task.CompletedTask;
        });
        _visibleSecond = visible2;
        return (visible, forbidden, type.Id);
    }

    private Guid _visibleSecond;

    private Task<HttpResponseMessage> PostAsync(Guid source, Guid target, Guid typeId)
        => _factory.CreateClient().PostAsJsonAsync("/graph/edges",
            new CreateGraphEdgeRequest(source, target, typeId),
            TestContext.Current.CancellationToken);

    // 陽性対照: 両端が見えれば張れる（否定形が「全部 404」で緑になっていないこと）。
    [Fact]
    public async Task Positive_control_an_edge_between_two_visible_documents_is_created()
    {
        var (visible, _, typeId) = await SeedAsync();
        _factory.ScopeProvider = _ => InternalOnly();

        var res = await PostAsync(visible, _visibleSecond, typeId);

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await res.Content.ReadFromJsonAsync<GraphEdgeCreatedDto>(TestContext.Current.CancellationToken);
        dto!.Provenance.Should().Be(EdgeProvenance.User, "この経路で作られる辺は必ず利用者付与である");
    }

    // 🔴 ADR-0034 決定 8: 権限外の文書へは張れない。**404**（403 ではない）。
    [Fact]
    public async Task Creating_an_edge_to_a_forbidden_document_is_not_found()
    {
        var (visible, forbidden, typeId) = await SeedAsync();
        _factory.ScopeProvider = _ => InternalOnly();

        var res = await PostAsync(visible, forbidden, typeId);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "403 を返すと『権限が無いだけで存在はする』ことが漏れる");
    }

    // 起点が見えない場合も張れない（終点だけ見ると、見えない文書から辺を生やせる）。
    [Fact]
    public async Task Creating_an_edge_from_a_forbidden_document_is_not_found()
    {
        var (visible, forbidden, typeId) = await SeedAsync();
        _factory.ScopeProvider = _ => InternalOnly();

        var res = await PostAsync(forbidden, visible, typeId);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 🔴 **権限外と不存在が区別できないこと。** 本文まで一致を要求する。
    // ここが割れると、辺を張る行為が存在確認の手段になる。
    [Fact]
    public async Task Forbidden_and_nonexistent_targets_are_indistinguishable()
    {
        var (visible, forbidden, typeId) = await SeedAsync();
        var nonexistent = Guid.NewGuid();
        _factory.ScopeProvider = _ => InternalOnly();

        var a = await PostAsync(visible, forbidden, typeId);
        var b = await PostAsync(visible, nonexistent, typeId);

        a.StatusCode.Should().Be(HttpStatusCode.NotFound);
        b.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await a.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Be(await b.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
                "応答本文に差があると、そこから権限外文書の存在が読める");
    }

    // FR-05: スコープが無ければ何も張れない（deny-by-default）。
    [Fact]
    public async Task Not_granted_scope_cannot_create_an_edge()
    {
        var (visible, _, typeId) = await SeedAsync();
        _factory.ScopeProvider = _ => new AccessScopeResponse("test-user", [], false);

        var res = await PostAsync(visible, _visibleSecond, typeId);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 型の実在は文書の可視性とは別の軸。**400**（404 にすると存在秘匿と混ざって読めなくなる）。
    [Fact]
    public async Task Unknown_edge_type_is_bad_request()
    {
        var (visible, _, _) = await SeedAsync();
        _factory.ScopeProvider = _ => InternalOnly();

        var res = await PostAsync(visible, _visibleSecond, Guid.NewGuid());

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Self_edge_is_bad_request()
    {
        var (visible, _, typeId) = await SeedAsync();
        _factory.ScopeProvider = _ => InternalOnly();

        var res = await PostAsync(visible, visible, typeId);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ADR-0033 決定 6: 同じ関係を二重に持たない。
    // ⚠ InMemory は一意索引を強制しないため、**ここで効いているのはアプリ層の事前検査だけ**である
    // （DB 層の一意制約が働くことは #941 で確認する）。
    [Fact]
    public async Task Duplicate_edge_is_conflict()
    {
        var (visible, _, typeId) = await SeedAsync();
        _factory.ScopeProvider = _ => InternalOnly();

        (await PostAsync(visible, _visibleSecond, typeId)).StatusCode
            .Should().Be(HttpStatusCode.Created);
        var second = await PostAsync(visible, _visibleSecond, typeId);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // IADR-0242 決定 9: 対称型は向きを問わず同一の辺になる（正規化）。
    // したがって逆向きの再作成も重複として弾かれる。
    [Fact]
    public async Task Symmetric_edge_created_in_reverse_is_also_a_duplicate()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var type = EdgeType.Create($"sym-{Guid.NewGuid():N}", EdgeTypeLayer.Core, isSymmetric: true);
        await _factory.SeedAsync(db =>
        {
            db.EdgeTypes.Add(type);
            db.Documents.Add(Node(a, "internal"));
            db.Documents.Add(Node(b, "internal"));
            return Task.CompletedTask;
        });
        _factory.ScopeProvider = _ => InternalOnly();

        (await PostAsync(a, b, type.Id)).StatusCode.Should().Be(HttpStatusCode.Created);
        var reverse = await PostAsync(b, a, type.Id);

        reverse.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "対称型は (min, max) へ正規化されるので、逆向きは同じ 1 行である");
    }

    // 未認証は 401（存在秘匿の前段）。
    [Fact]
    public async Task Unauthenticated_is_unauthorized()
    {
        var (visible, _, typeId) = await SeedAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");

        var res = await client.PostAsJsonAsync("/graph/edges",
            new CreateGraphEdgeRequest(visible, _visibleSecond, typeId),
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
