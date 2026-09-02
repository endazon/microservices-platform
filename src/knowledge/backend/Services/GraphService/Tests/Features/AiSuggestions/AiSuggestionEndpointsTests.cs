using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GraphService.Domain;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GraphService.Tests.Features.AiSuggestions;

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
    private async Task ReadAsync(Func<GraphService.Infrastructure.Persistence.GraphDbContext, Task> read)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<GraphService.Infrastructure.Persistence.GraphDbContext>();
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
        SuggestionState.IsValid(GraphService.Features.AiSuggestions.AiSuggestionEndpoints.AnyState)
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

    // ---- #1104（SC-03 の文書での絞り込み）が要求する一覧の形 ----

    // 同じ文書を複数の提案が共有する形を作る（本節の題材はまさにそれである）。
    // `SeedLinkAsync` は端点の文書も毎回追加するため、共有する文書は先にここで 1 度だけ入れる。
    private Task SeedDocumentsAsync(params (Guid Id, string Conf)[] documents)
        => _factory.SeedAsync(db =>
        {
            foreach (var (id, conf) in documents) db.Documents.Add(Doc(id, conf));
            return Task.CompletedTask;
        });

    // 端点の文書は既に在る前提で、リンク提案だけを追加する。
    private Task SeedLinkOnlyAsync(Guid source, Guid target)
        => _factory.SeedAsync(db =>
        {
            var type = EdgeType.Create($"t-{Guid.NewGuid():N}", EdgeTypeLayer.Core, false);
            db.EdgeTypes.Add(type);
            db.AiSuggestions.Add(
                AiSuggestion.CreateLink(source, target, type.Id, "根拠", DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        });

    // 同上（タグ提案）。
    private Task SeedTagOnlyAsync(Guid document, string tagValue)
        => _factory.SeedAsync(db =>
        {
            db.AiSuggestions.Add(
                AiSuggestion.CreateTag(document, tagValue, "根拠", DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        });

    // FR-18, SC-03 (#1104): `documentId` を渡すと**その文書を端点に持つ提案だけ**が返る。
    //
    // 🔴 **陽性対照と陰性対照を対で置く。** 「関係するものが返る」だけを見ると、
    // `Where` を消しても（＝全件返す実装でも）緑のままである。**同じ種を `documentId` 無しで
    // 引いて無関係な提案が返ることまで見る**（絞りが効いていることの検出力）。
    [Fact]
    public async Task Filtering_by_document_returns_only_suggestions_that_touch_it()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var subject = Guid.NewGuid();
        var neighbour = Guid.NewGuid();
        var predecessor = Guid.NewGuid();
        await SeedDocumentsAsync(
            (subject, "internal"), (neighbour, "internal"), (predecessor, "internal"));
        // 起点が当該文書 / 終点が当該文書 / タグ提案（終点なし） の 3 通りを拾えること。
        await SeedLinkOnlyAsync(subject, neighbour);
        await SeedLinkOnlyAsync(predecessor, subject);
        await SeedTagOnlyAsync(subject, "経理");
        // 当該文書に触れない提案（返ってはならない）。
        var unrelatedSource = Guid.NewGuid();
        await SeedLinkAsync(unrelatedSource, Guid.NewGuid());

        var filtered = await ListAsync($"?documentId={subject}");

        filtered.Should().HaveCount(3, "起点・終点・タグ提案の 3 件が当該文書に触れる");
        filtered.Should().OnlyContain(
            i => i.SourceDocumentId == subject || i.TargetDocumentId == subject);
        filtered.Should().NotContain(i => i.SourceDocumentId == unrelatedSource);

        // 陽性対照: **絞らなければ無関係な提案も返る**（＝上の否定形は自明ではない）。
        var unfiltered = await ListAsync("");
        unfiltered.Should().Contain(i => i.SourceDocumentId == unrelatedSource,
            "documentId 無しは従来どおり権限内の全件を返す（SC-21 の一覧は絞らない）");
    }

    // 🔴 FR-18, SC-03, ADR-0034 決定 1・2 (#1104): **絞り込みを足しても ABAC は落ちていない。**
    //
    // 絞りは可視性解決の**前段**にあり、判定そのものは端点の文書属性で行われる。
    // **陽性対照（見える文書の提案は返る）と陰性対照（見えない端点を持つ提案は
    // documentId で名指ししても返らない）を対で置く。**
    [Fact]
    public async Task Filtering_by_document_still_hides_invisible_endpoints()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var subject = Guid.NewGuid();
        var visibleNeighbour = Guid.NewGuid();
        var hiddenNeighbour = Guid.NewGuid();
        await SeedDocumentsAsync(
            (subject, "internal"), (visibleNeighbour, "internal"), (hiddenNeighbour, "restricted"));
        // 陽性対照: 両端とも internal（見える）。
        await SeedLinkOnlyAsync(subject, visibleNeighbour);
        // 陰性対照: 当該文書は見えるが、**終点が restricted**（見えない）。
        await SeedLinkOnlyAsync(subject, hiddenNeighbour);

        var items = await ListAsync($"?documentId={subject}");

        items.Should().Contain(i => i.TargetDocumentId == visibleNeighbour,
            "陽性対照: 権限のある文書の提案は返る（装置が空振りしていない）");
        items.Should().NotContain(i => i.TargetDocumentId == hiddenNeighbour,
            "陰性対照: 端点が見えない提案は、documentId で名指ししても件数にすら現れない");
    }

    // 🔴 FR-18, SC-03, ADR-0034 決定 2, IADR-0009 (#1104): **存在秘匿と整合する。**
    //
    // 「その文書は無い」「その文書は見えない」「その文書の提案は 0 件」の 3 つが
    // **応答として区別できてはならない**。3 通りとも 200 ＋ 空配列であることを固定する。
    [Fact]
    public async Task An_unauthorized_document_id_is_indistinguishable_from_one_with_no_suggestions()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        // ① 実在するが、スコープ外（restricted）の文書。提案も 1 件ある。
        var unauthorized = Guid.NewGuid();
        await SeedLinkAsync(unauthorized, Guid.NewGuid(),
            sourceConf: "restricted", targetConf: "restricted");
        // ② 実在し、見えるが、提案が 1 件も無い文書。
        var visibleWithoutSuggestions = Guid.NewGuid();
        await _factory.SeedAsync(db =>
        {
            db.Documents.Add(Doc(visibleWithoutSuggestions, "internal"));
            return Task.CompletedTask;
        });
        // ③ そもそも存在しない文書。
        var nonExistent = Guid.NewGuid();
        // 🔴 **検出力**: 見える提案を 1 件置く。これが無いと DB が空でも 3 通りとも空になり、
        // 絞りを消しても本テストは緑のまま通る（「空だった」を「隠せている」と読む事故）。
        var decoy = Guid.NewGuid();
        await SeedLinkAsync(decoy, Guid.NewGuid());
        (await ListAsync($"?documentId={decoy}")).Should().ContainSingle(
            "陽性対照: 絞りも可視性も効いていて、漏れ得る中身が実在する");

        var forUnauthorized = await ListAsync($"?documentId={unauthorized}");
        var forEmpty = await ListAsync($"?documentId={visibleWithoutSuggestions}");
        var forNonExistent = await ListAsync($"?documentId={nonExistent}");

        forUnauthorized.Should().BeEmpty();
        forEmpty.Should().BeEmpty();
        forNonExistent.Should().BeEmpty();
        // ListAsync は 200 を assert している。**3 通りとも状態コードも本文も同じ**である。
    }

    // #1104: 形式不正の `documentId` は 400（値域の検査ではなく束縛の失敗）。
    // **存在は漏れない** —— UUID でない文字列はどの文書も指さないためである。
    [Fact]
    public async Task A_malformed_document_id_is_rejected()
    {
        var res = await _factory.CreateClient()
            .GetAsync("/graph/suggestions/?documentId=not-a-guid",
                TestContext.Current.CancellationToken);

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
