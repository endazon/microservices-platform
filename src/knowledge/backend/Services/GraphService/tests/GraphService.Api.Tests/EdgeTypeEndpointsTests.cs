using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GraphService.Api.Foundation.Domain;
using Knowledge.Contracts.Dtos;

namespace GraphService.Api.Tests;

// FR-17, SC-09, SC-10, ADR-0033 決定 3・9, IADR-0242 決定 7・8: 辺の型辞書 API（#910）。
//
// ⚠ **InMemory プロバイダは一意索引も外部キーも強制しない。** したがって本クラスが試験できるのは
// **アプリ層のガード**（事前検査 → 409）までである。**DB 層の防壁**（`ON DELETE RESTRICT`・
// 一意制約・競合の 409 変換）は実 PostgreSQL が要るため `Knowledge.IntegrationTests` 側に置く。
// この切り分けを書かずに InMemory だけで緑にすると「DB 層の防壁を試験した」と誤認する。
public class EdgeTypeEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public EdgeTypeEndpointsTests(TestWebApplicationFactory factory) => _factory = factory;

    private HttpClient Admin() => _factory.CreateClient();

    private HttpClient WithRoles(string roles)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return c;
    }

    private async Task<Guid> SeedTypeAsync(string name, int edges = 0)
    {
        var type = EdgeType.Create(name, EdgeTypeLayer.Core, false);
        await _factory.SeedAsync(db =>
        {
            db.EdgeTypes.Add(type);
            for (var i = 0; i < edges; i++)
                db.Edges.Add(Edge.Create(Guid.NewGuid(), Guid.NewGuid(), type.Id, false, EdgeProvenance.Auto));
            return Task.CompletedTask;
        });
        return type.Id;
    }

    // FR-17, SC-09: 追加できる。識別子は辞書が採番する。
    [Fact]
    public async Task Create_adds_a_type_with_zero_usage()
    {
        var res = await Admin().PostAsJsonAsync("/graph/edge-types",
            new CreateEdgeTypeRequest($"c-{Guid.NewGuid():N}", EdgeTypeLayer.Recommended, false),
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await res.Content.ReadFromJsonAsync<EdgeTypeDto>(TestContext.Current.CancellationToken);
        dto!.UsageCount.Should().Be(0);
        dto.IsSeed.Should().BeFalse("API から足した型は seed ではない");
    }

    // SC-09: 同名は 409。**正規化後**（前後の空白を除いた形）で比較する。
    [Fact]
    public async Task Create_with_a_duplicate_name_is_conflict_even_with_surrounding_whitespace()
    {
        var name = $"dup-{Guid.NewGuid():N}";
        await SeedTypeAsync(name);

        var res = await Admin().PostAsJsonAsync("/graph/edge-types",
            new CreateEdgeTypeRequest($"  {name}  ", EdgeTypeLayer.Core, false),
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "前後の空白だけが違う名前を通すと、辞書が実質的に重複を許す");
    }

    [Fact]
    public async Task Create_with_an_invalid_layer_is_bad_request()
    {
        var res = await Admin().PostAsJsonAsync("/graph/edge-types",
            new CreateEdgeTypeRequest($"x-{Guid.NewGuid():N}", "misc", false),
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // 🔴 ADR-0033 決定 9: 改名しても**既存の辺は 1 行も変わらない**（識別子を参照しているため）。
    [Fact]
    public async Task Rename_does_not_touch_any_edge_row()
    {
        var id = await SeedTypeAsync($"r-{Guid.NewGuid():N}", edges: 3);

        var res = await Admin().PutAsJsonAsync($"/graph/edge-types/{id}",
            new RenameEdgeTypeRequest($"renamed-{Guid.NewGuid():N}"),
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<EdgeTypeDto>(TestContext.Current.CancellationToken);
        dto!.Id.Should().Be(id, "改名で識別子が変わると既存の辺の参照が全部切れる");
        dto.UsageCount.Should().Be(3, "改名は辺に触れないので使用件数は変わらない");
    }

    [Fact]
    public async Task Rename_to_an_existing_name_is_conflict()
    {
        var taken = $"t-{Guid.NewGuid():N}";
        await SeedTypeAsync(taken);
        var id = await SeedTypeAsync($"o-{Guid.NewGuid():N}");

        var res = await Admin().PutAsJsonAsync($"/graph/edge-types/{id}",
            new RenameEdgeTypeRequest(taken), TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // 同じ名前への改名（実質の no-op）は 409 にしない —— 409 にしても管理者は何も直せない。
    [Fact]
    public async Task Rename_to_the_same_name_is_allowed()
    {
        var name = $"same-{Guid.NewGuid():N}";
        var id = await SeedTypeAsync(name);

        var res = await Admin().PutAsJsonAsync($"/graph/edge-types/{id}",
            new RenameEdgeTypeRequest(name), TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 🔴 ADR-0033 決定 9: 参照が 1 件でもあれば削除拒否。**件数を添える。**
    [Fact]
    public async Task Delete_a_referenced_type_is_conflict_with_the_usage_count()
    {
        var id = await SeedTypeAsync($"used-{Guid.NewGuid():N}", edges: 4);

        var res = await Admin().DeleteAsync($"/graph/edge-types/{id}", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "アプリ層の事前カウントを外すと、InMemory では RESTRICT が効かないため "
            + "204 で黙って消える（変異試験で実測）。実 PostgreSQL なら RESTRICT が 500 として現れる。"
            + "どちらの環境でも、守っているのはこの事前カウントである");
        var body = await res.Content.ReadFromJsonAsync<EdgeTypeInUseResponse>(TestContext.Current.CancellationToken);
        body!.UsageCount.Should().Be(4, "数だけでも『まず 4 本を付け替えてから消す』と行動できる");
        body.Error.Should().Be("edge_type_in_use");
    }

    // 陽性対照: 参照が無ければ削除できる（「全部 409」で緑になっていないこと）。
    [Fact]
    public async Task Positive_control_an_unreferenced_type_can_be_deleted()
    {
        var id = await SeedTypeAsync($"free-{Guid.NewGuid():N}", edges: 0);

        var res = await Admin().DeleteAsync($"/graph/edge-types/{id}", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // 🔴 SC-09/SC-10: **一覧と削除ガードは同一の数を返す。**
    // 食い違うと「一覧が 0 件と言ったのに削除が 409」になり、管理者は辞書を信用できなくなる。
    [Fact]
    public async Task List_usage_count_matches_the_delete_guard()
    {
        var id = await SeedTypeAsync($"m-{Guid.NewGuid():N}", edges: 2);

        var list = await Admin().GetFromJsonAsync<List<EdgeTypeDto>>(
            "/graph/edge-types", TestContext.Current.CancellationToken);
        var listed = list!.Single(t => t.Id == id).UsageCount;

        var del = await Admin().DeleteAsync($"/graph/edge-types/{id}", TestContext.Current.CancellationToken);
        var guard = (await del.Content.ReadFromJsonAsync<EdgeTypeInUseResponse>(
            TestContext.Current.CancellationToken))!.UsageCount;

        listed.Should().Be(guard);
        listed.Should().Be(2);
    }

    // SC-10: 一覧は初期値集合（seed）も使用件数つきで返す。
    [Fact]
    public async Task List_includes_seeded_types_with_usage()
    {
        var list = await Admin().GetFromJsonAsync<List<EdgeTypeDto>>(
            "/graph/edge-types", TestContext.Current.CancellationToken);

        list.Should().NotBeNull();
        list!.Should().Contain(t => t.Name == "related" && t.IsSymmetric && t.IsSeed);
    }

    // SC-09: 書きは管理者のみ。運用者では通らない。
    [Fact]
    public async Task Write_requires_admin_role()
    {
        var res = await WithRoles("platform-operator").PostAsJsonAsync("/graph/edge-types",
            new CreateEdgeTypeRequest($"n-{Guid.NewGuid():N}", EdgeTypeLayer.Core, false),
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // SC-10: 読みは運用者にも開く（運用ダッシュボードが型別使用件数を表示するため）。
    [Fact]
    public async Task Read_is_open_to_operator_role()
    {
        var res = await WithRoles("platform-operator")
            .GetAsync("/graph/edge-types", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 一般利用者は読めない。
    [Fact]
    public async Task Read_is_denied_for_a_plain_user()
    {
        var res = await WithRoles("viewer")
            .GetAsync("/graph/edge-types", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
