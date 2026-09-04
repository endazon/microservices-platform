using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GraphService.Domain;
using Knowledge.Contracts.Dtos;

namespace GraphService.Tests.Features.EdgeTypes.Catalog;

// FR-17, ADR-0033, ADR-0039 (#962): 描画用の辺の型カタログ。
//
// **本クラスが覆うのは FR-17（グラフ機能）である。** 消費者となるグラフビュー画面は未実装で
// テスト仕様書も無いため、画面の ID をここから参照しない（参照すると「画面の受け入れ基準を
// 試験している」と主張することになり、逆方向のトレーサビリティ検査が正しく止める）。
//
// 🔴 **既存の `/graph/edge-types`（admin / operator 限定・使用件数つき）とは別物である。**
// グラフビュー画面は一般利用者向けであり、辺の型名が引けないと描き分けも型フィルタも描けない
// （グラフ応答が返すのは `EdgeTypeId` だけである）。しかし既存口をそのまま開けると
// **ABAC で絞られていない使用件数**が漏れる。だから件数を持たない口を分けた。
[Trait("TestKind", "Integration")]
public class EdgeTypeCatalogTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public EdgeTypeCatalogTests(TestWebApplicationFactory factory) => _factory = factory;

    private HttpClient WithRoles(string roles)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return c;
    }

    // C-06 陽性対照: ロールを持たない一般利用者でカタログが引ける。
    //
    // 🔴 **この陽性対照が本クラスの要である。** 否定形（未認証で 401）だけを並べると、
    // 「ロール要求を足す」変異でも 401 のテストは緑のままになる。
    [Fact]
    public async Task Catalog_is_readable_by_a_plain_user()
    {
        var name = $"cat-{Guid.NewGuid():N}";
        await _factory.SeedAsync(db =>
        {
            db.EdgeTypes.Add(EdgeType.Create(name, EdgeTypeLayer.Core, false));
            return Task.CompletedTask;
        });

        var res = await WithRoles("viewer")
            .GetAsync("/graph/edge-types/catalog", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK,
            "グラフビューは一般利用者の画面であり、型名が引けないと辺を描き分けられない");
        var items = await res.Content.ReadFromJsonAsync<List<EdgeTypeCatalogItemDto>>(
            TestContext.Current.CancellationToken);
        items!.Select(i => i.Name).Should().Contain(name);
    }

    // 🔴 C-02: 応答に使用件数が現れない。
    //
    // 使用件数は `db.Edges.CountAsync(...)` で**全辺を数えて**おり ABAC で絞られていない。
    // ホップごと ABAC（IADR-0242）が個々のノード・辺を隠しているのに、集計値が総量を漏らす。
    //
    // **本文の文字列で見る。** 型（`EdgeTypeCatalogItemDto`）に無いことは、
    // 直列化される形に無いことの証明にならない（匿名型へ差し替えれば型を通さず漏らせる）。
    [Fact]
    public async Task Catalog_never_exposes_usage_counts()
    {
        // 辺を実際に張る。件数が 0 だと「漏れていない」のか「数えるものが無い」のか区別できない。
        var type = EdgeType.Create($"cat-{Guid.NewGuid():N}", EdgeTypeLayer.Core, false);
        await _factory.SeedAsync(db =>
        {
            db.EdgeTypes.Add(type);
            for (var i = 0; i < 3; i++)
                db.Edges.Add(Edge.Create(Guid.NewGuid(), Guid.NewGuid(), type.Id, false, EdgeProvenance.Auto));
            return Task.CompletedTask;
        });

        var body = await WithRoles("viewer")
            .GetStringAsync("/graph/edge-types/catalog", TestContext.Current.CancellationToken);

        body.Should().NotContain("usageCount",
            "使用件数は ABAC で絞られておらず、一般利用者へ返すと権限外の辺を含む総量が漏れる");
        body.Should().NotContain("UsageCount");
    }

    // 装置の検出力: 既存の管理者向け一覧は使用件数を**返している**。
    // これが無いと、上の否定形は「そもそもどこにも件数が無い」だけで緑になり得る。
    [Fact]
    public async Task The_admin_listing_does_expose_usage_counts_so_the_negative_test_has_teeth()
    {
        var type = EdgeType.Create($"cat-{Guid.NewGuid():N}", EdgeTypeLayer.Core, false);
        await _factory.SeedAsync(db =>
        {
            db.EdgeTypes.Add(type);
            db.Edges.Add(Edge.Create(Guid.NewGuid(), Guid.NewGuid(), type.Id, false, EdgeProvenance.Auto));
            return Task.CompletedTask;
        });

        var body = await _factory.CreateClient()
            .GetStringAsync("/graph/edge-types", TestContext.Current.CancellationToken);

        body.Should().Contain("usageCount",
            "管理者向け一覧が件数を返していないなら、カタログ側の否定形テストは検出力を持たない");
    }

    // FR-04, FR-17, ADR-0035 決定 2 (#970) W-01: カタログが**辺の型の重み**を運ぶ。
    // 二段検索の再ランク（RetrievalService）が唯一この口から重みを引く —— ここに載らない限り
    // 「型ごとの重み付け」（ADR-0035 決定 2）は本番で効かない（IADR-0263 決定 6 の解消点）。
    //
    // 🔴 **リテラルで測る**（seed の定数と突き合わせるとトートロジーになる。#947a のテストと同じ作法）。
    [Fact]
    public async Task Catalog_carries_the_edge_type_weights()
    {
        var items = (await WithRoles("viewer").GetFromJsonAsync<List<EdgeTypeCatalogItemDto>>(
            "/graph/edge-types/catalog", TestContext.Current.CancellationToken))!;

        // ADR-0035 決定 2 が名指しした 2 つを固定する。
        items.Single(i => i.Name == "supersedes").Weight.Should().Be(1.0, "最新版へ強く誘導する");
        items.Single(i => i.Name == "related").Weight.Should().Be(0.3, "弱く扱う");

        // W-02 装置の検出力: 差が付いていること。全部同じ値なら重みを公開した意味が無い。
        items.Single(i => i.Name == "supersedes").Weight
            .Should().BeGreaterThan(items.Single(i => i.Name == "related").Weight);
    }

    // W-01 系: API で追加した型（重み指定の口が無い）はカタログでも既定重み 0.5 で読める。
    // RetrievalService 側のフォールバック値（辞書に無い型 = 0.5）と同じ値であること。
    [Fact]
    public async Task Catalog_reports_the_default_weight_for_a_newly_created_type()
    {
        var name = $"cat-{Guid.NewGuid():N}";
        await _factory.SeedAsync(db =>
        {
            db.EdgeTypes.Add(EdgeType.Create(name, EdgeTypeLayer.Core, false));
            return Task.CompletedTask;
        });

        var items = (await WithRoles("viewer").GetFromJsonAsync<List<EdgeTypeCatalogItemDto>>(
            "/graph/edge-types/catalog", TestContext.Current.CancellationToken))!;

        items.Single(i => i.Name == name).Weight.Should().Be(0.5, "新規追加の既定は中庸（0.5）である");
    }

    // C-03: 未認証は 401。
    [Fact]
    public async Task Catalog_requires_authentication()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");

        var res = await client.GetAsync("/graph/edge-types/catalog", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
