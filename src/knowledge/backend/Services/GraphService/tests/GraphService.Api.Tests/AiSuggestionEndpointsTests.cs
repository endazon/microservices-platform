using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GraphService.Api.Foundation.Domain;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GraphService.Api.Tests;

// FR-18, ADR-0033 決定 7・10: AI 提案の承認・却下（#914）。
//
// **［2026-08-23 追記 / #918］SC-21（AI 提案一覧）の実装に伴い、一覧の口が画面の受け入れ基準を
// 直接支えるようになったため、一覧に関するテストは SC-21 も参照する。** 承認・却下の状態遷移
// （#914 の射程）は従来どおり FR-18 だけを参照する。テスト仕様書は `docs/tests/SC-21_*.md`。
public class AiSuggestionEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public AiSuggestionEndpointsTests(TestWebApplicationFactory factory) => _factory = factory;

    // 既定 scope は「条件無し＝全許可」なので、可視性を測るテストは必ず絞る。
    private static Platform.Shared.Contracts.Dtos.AccessScopeResponse InternalOnly()
        => new("test-user",
            [new Platform.Shared.Contracts.Dtos.AttributeFilter("confidentiality", ["internal"])], true);

    // 読み戻し。TestWebApplicationFactory は Seed しか持たないため、既存テストと同じ作法で開く。
    private async Task ReadAsync(Func<GraphService.Api.Foundation.Persistence.GraphDbContext, Task> read)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<GraphService.Api.Foundation.Persistence.GraphDbContext>();
        await read(db);
    }

    private static GraphDocument Doc(Guid id, string conf)
        => GraphDocument.Create(id, $"doc-{id:N}",
            new Dictionary<string, string> { ["confidentiality"] = conf },
            null, DateTimeOffset.UtcNow);

    private async Task<(Guid SuggestionId, Guid TypeId)> SeedLinkAsync(
        Guid source, Guid target, string sourceConf = "internal", string targetConf = "internal")
    {
        var type = EdgeType.Create($"t-{Guid.NewGuid():N}", EdgeTypeLayer.Core, false);
        var s = AiSuggestion.CreateLink(source, target, type.Id, "根拠", DateTimeOffset.UtcNow);
        await _factory.SeedAsync(db =>
        {
            db.EdgeTypes.Add(type);
            db.Documents.Add(Doc(source, sourceConf));
            db.Documents.Add(Doc(target, targetConf));
            db.AiSuggestions.Add(s);
            return Task.CompletedTask;
        });
        return (s.Id, type.Id);
    }

    // S-03 陽性対照: 承認すると ai-approved の辺が 1 本できる。
    [Fact]
    public async Task Approving_a_link_suggestion_creates_exactly_one_ai_approved_edge()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var (id, typeId) = await SeedLinkAsync(Guid.NewGuid(), Guid.NewGuid());

        var res = await _factory.CreateClient().PostAsync(
            $"/graph/suggestions/{id}/approve", null, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<AiSuggestionDto>(
            TestContext.Current.CancellationToken);
        dto!.State.Should().Be(SuggestionState.Approved);

        await ReadAsync(db =>
        {
            var edges = db.Edges.Where(e => e.EdgeTypeId == typeId).ToList();
            edges.Should().HaveCount(1);
            edges[0].Provenance.Should().Be(EdgeProvenance.AiApproved);
            return Task.CompletedTask;
        });
    }

    // S-05 🔴 pending / rejected の提案は辺を 1 本も作らない。
    //
    // **これは探索側のフィルタで実現していない。** `EdgeProvenance` は `ai-approved` しか
    // 持たず、承認して初めて辺が生まれる —— **絞る対象が存在しない**のが正しい形である。
    // 「実装しなかった」のではなく「構造的に不要」であることを、ここで固定する。
    [Fact]
    public async Task Unapproved_suggestions_produce_no_edges_at_all()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var (pendingId, pendingType) = await SeedLinkAsync(Guid.NewGuid(), Guid.NewGuid());
        var (rejectedId, rejectedType) = await SeedLinkAsync(Guid.NewGuid(), Guid.NewGuid());

        await _factory.CreateClient().PostAsJsonAsync(
            $"/graph/suggestions/{rejectedId}/reject",
            new RejectAiSuggestionRequest("s1", "t1"), TestContext.Current.CancellationToken);

        await ReadAsync(db =>
        {
            db.Edges.Count(e => e.EdgeTypeId == pendingType).Should().Be(0, "pending は辺にならない");
            db.Edges.Count(e => e.EdgeTypeId == rejectedType).Should().Be(0, "rejected も辺にならない");
            return Task.CompletedTask;
        });
        pendingId.Should().NotBeEmpty();
    }

    // S-04 🔴 承認は #913 と同じ到達可能性の検証を通す。
    //
    // 見えない文書へ辺を張れると、辺の存在から文書の存在が漏れる。
    [Fact]
    public async Task Approving_is_refused_when_an_endpoint_is_not_visible()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        // scope は confidentiality=internal のみを許す。target を restricted にして不可視にする。
        var (id, typeId) = await SeedLinkAsync(
            Guid.NewGuid(), Guid.NewGuid(), sourceConf: "internal", targetConf: "restricted");

        var res = await _factory.CreateClient().PostAsync(
            $"/graph/suggestions/{id}/approve", null, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "権限外は 404 に倒す（403 は『権限が無いだけで存在はする』ことを漏らす）");
        await ReadAsync(db =>
        {
            db.Edges.Count(e => e.EdgeTypeId == typeId).Should().Be(0, "拒んだのに辺ができてはならない");
            return Task.CompletedTask;
        });
    }

    // 不正な遷移は 409。承認済みを再承認しても辺が二重にならない。
    [Fact]
    public async Task Re_approving_is_a_conflict_and_does_not_duplicate_the_edge()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var (id, typeId) = await SeedLinkAsync(Guid.NewGuid(), Guid.NewGuid());
        var client = _factory.CreateClient();

        await client.PostAsync($"/graph/suggestions/{id}/approve", null,
            TestContext.Current.CancellationToken);
        var second = await client.PostAsync($"/graph/suggestions/{id}/approve", null,
            TestContext.Current.CancellationToken);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await ReadAsync(db =>
        {
            db.Edges.Count(e => e.EdgeTypeId == typeId).Should().Be(1);
            return Task.CompletedTask;
        });
    }

    // 一覧の既定は pending。権限外の文書に関する提案は現れない。
    [Fact]
    public async Task The_listing_defaults_to_pending_and_hides_invisible_suggestions()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var visibleTarget = Guid.NewGuid();
        var (_, _) = await SeedLinkAsync(Guid.NewGuid(), visibleTarget);
        var hiddenSource = Guid.NewGuid();
        await SeedLinkAsync(hiddenSource, Guid.NewGuid(), sourceConf: "restricted");

        var res = await _factory.CreateClient()
            .GetAsync("/graph/suggestions/", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await res.Content.ReadFromJsonAsync<List<AiSuggestionDto>>(
            TestContext.Current.CancellationToken);
        items!.Should().Contain(i => i.TargetDocumentId == visibleTarget, "見える提案は出る（陽性対照）");
        items!.Should().NotContain(i => i.SourceDocumentId == hiddenSource,
            "権限のない文書に関する提案は一覧にも件数にも現れない");
    }

    // 🔴 S-10: **一括承認の口が存在しない。**
    //
    // FR-18 が明示的に禁じている（画面仕様の「描いてはいけないもの」にも入る）。理由は
    // 「タイトルだけを見て機械的に承認する運用に落ちる」であり、**後から誰かが親切心で
    // 足しかねない**ため、機械で見張る。ルート表を実際に走査する。
    [Fact]
    public void No_bulk_approval_route_exists()
    {
        using var scope = _factory.Services.CreateScope();
        var sources = scope.ServiceProvider.GetRequiredService<IEnumerable<EndpointDataSource>>();
        var routes = sources.SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .ToList();

        routes.Should().NotBeEmpty("ルートが 1 本も取れないと本テストは空振りする");
        routes.Should().Contain(r => r.Contains("/graph/suggestions", StringComparison.Ordinal),
            "提案の口が無いなら、下の否定形は自明に成り立ってしまう（装置の検出力）");

        var bulk = routes.Where(r =>
            r.Contains("suggestions", StringComparison.OrdinalIgnoreCase)
            && (r.Contains("bulk", StringComparison.OrdinalIgnoreCase)
                || r.Contains("approve-all", StringComparison.OrdinalIgnoreCase)
                || r.Contains("batch", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        bulk.Should().BeEmpty(
            "FR-18 は一括承認を禁じている。"
            + "一覧の 1 行に収まる情報では承認を判断できないためである");

        // 承認・却下は **id を取る**（＝ 1 件ずつ）ことも固定する。
        var approve = routes.Where(r => r.Contains("approve", StringComparison.OrdinalIgnoreCase)).ToList();
        approve.Should().NotBeEmpty();
        approve.Should().OnlyContain(r => r.Contains("{id:guid}", StringComparison.Ordinal),
            "id を取らない承認の口は、実質の一括承認になり得る");
    }

    // ---- #918（SC-21 AI 提案一覧）が要求する一覧の形 ----

    private async Task<Guid> SeedTagAsync(Guid document, string tagValue, string conf = "internal")
    {
        var s = AiSuggestion.CreateTag(document, tagValue, "根拠", DateTimeOffset.UtcNow);
        await _factory.SeedAsync(db =>
        {
            db.Documents.Add(Doc(document, conf));
            db.AiSuggestions.Add(s);
            return Task.CompletedTask;
        });
        return s.Id;
    }

    // FR-18, SC-21 主要素 1: 一覧は**両端の文書名**を運ぶ。ID だけでは「提案の内容」列を描けない。
    [Fact]
    public async Task The_listing_carries_both_endpoint_titles()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        await SeedLinkAsync(source, target);

        var items = await ListAsync("?state=pending&kind=link");

        var row = items.Single(i => i.SourceDocumentId == source);
        row.SourceDocumentTitle.Should().Be($"doc-{source:N}");
        row.TargetDocumentTitle.Should().Be($"doc-{target:N}");
    }

    // FR-18, SC-21: タグ提案は終点を持たない。**終点の名前は null であって空文字ではない**
    // （空文字だと「名前の無い文書がある」と読める）。
    [Fact]
    public async Task A_tag_suggestion_carries_no_target_title()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var document = Guid.NewGuid();
        await SeedTagAsync(document, "経理");

        var items = await ListAsync("?state=pending&kind=tag");

        var row = items.Single(i => i.SourceDocumentId == document);
        row.SourceDocumentTitle.Should().Be($"doc-{document:N}");
        row.TargetDocumentTitle.Should().BeNull();
        row.TagValue.Should().Be("経理");
    }

    // FR-18, SC-21 入力/バリデーション: 状態フィルタの 4 値目「すべて」。
    //
    // 🔴 **陽性対照と対で置く** —— 既定（pending）でも同じ件数が返る実装だと、
    // 「すべてが返る」だけを見るテストは緑のまま通る。
    [Fact]
    public async Task State_all_returns_every_state_while_the_default_returns_only_pending()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var pendingSource = Guid.NewGuid();
        await SeedLinkAsync(pendingSource, Guid.NewGuid());
        var approvedSource = Guid.NewGuid();
        var (approvedId, _) = await SeedLinkAsync(approvedSource, Guid.NewGuid());
        await _factory.CreateClient().PostAsync($"/graph/suggestions/{approvedId}/approve", null,
            TestContext.Current.CancellationToken);

        var defaults = await ListAsync("");
        var all = await ListAsync("?state=all");

        // 陽性対照: 既定は pending だけを返す。
        defaults.Should().Contain(i => i.SourceDocumentId == pendingSource);
        defaults.Should().NotContain(i => i.SourceDocumentId == approvedSource,
            "既定は pending であり、承認済みは含まれない");
        // 本題: すべてを返す。
        all.Should().Contain(i => i.SourceDocumentId == pendingSource);
        all.Should().Contain(i => i.SourceDocumentId == approvedSource,
            "『すべて』は状態の絞りを外す");
    }

    // 🔴 `all` は**フィルタの解除**であって状態の値ではない。永続層へ書ける状態にしない。
    [Fact]
    public void All_is_not_a_persistable_state()
    {
        SuggestionState.IsValid(GraphService.Api.Foundation.Endpoints.AiSuggestionEndpoints.AnyState)
            .Should().BeFalse("all を状態の値集合へ入れると、行の State 列へ書けてしまう");
    }

    // 値域の検査は生きている（`all` を通したことで穴が開いていない）。**陽性対照の対**。
    [Fact]
    public async Task An_unknown_state_is_still_rejected()
    {
        var res = await _factory.CreateClient()
            .GetAsync("/graph/suggestions/?state=maybe", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<List<AiSuggestionDto>> ListAsync(string query)
    {
        var res = await _factory.CreateClient()
            .GetAsync($"/graph/suggestions/{query}", TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<List<AiSuggestionDto>>(
            TestContext.Current.CancellationToken))!;
    }
}
