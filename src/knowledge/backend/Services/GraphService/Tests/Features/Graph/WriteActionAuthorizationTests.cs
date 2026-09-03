using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GraphService.Domain;
using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Tests.Features.Graph;

// FR-05, FR-17, FR-18, FR-21, ADR-0034 決定 8, ADR-0036 D-07, IADR-0272 (#993):
// 🔴 **「読めるなら書ける」を否定形で固定する。**
//
// 従前、書き込み経路（辺の付与・提案の承認／却下）は**読み取りの認可スコープ**で判定していた。
// 契約へ `Action` が入っても（IADR-0253 決定 5 / #989）、**呼び出し側が渡さなければ既定の read の
// ままである** —— 後方互換は「無改修を許す」という意味であって、是正ではない。
//
// 🔴 **否定形は陽性対照と対でなければ意味が無い。** 「常に拒む実装」でも否定形だけなら緑になる。
// 本クラスは 3 経路すべてについて、**拒む側と通す側を対で置く。**
public class WriteActionAuthorizationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public WriteActionAuthorizationTests(TestWebApplicationFactory factory) => _factory = factory;

    // 「マッチするポリシーがあり、機密区分 internal に限る」スコープ。
    private static AccessScopeResponse InternalOnly()
        => new("test-user", [new AttributeFilter("confidentiality", ["internal"])], true);

    // 「マッチするポリシーが 1 件も無い」＝ deny-by-default。
    // **write ポリシーが 1 件も登録されていない環境の write スコープはこれである**
    // （AuthorizationService 側の AccessScopeContractTests が固定している）。
    private static AccessScopeResponse Denied() => new("test-user", [], false);

    // 「マッチはするが、public の文書に限る」write スコープ。**Granted は true である。**
    private static AccessScopeResponse PublicOnly()
        => new("test-user", [new AttributeFilter("confidentiality", ["public"])], true);

    private static GraphDocument Doc(Guid id, string conf)
        => GraphDocument.Create(id, $"doc-{id:N}",
            new Dictionary<string, string> { ["confidentiality"] = conf },
            null, DateTimeOffset.UtcNow);

    private async Task ReadAsync(Func<GraphDbContext, Task> read)
    {
        using var scope = _factory.Services.CreateScope();
        await read(scope.ServiceProvider.GetRequiredService<GraphDbContext>());
    }

    // 両端とも internal（read の InternalOnly で可視）の 2 文書 ＋ 辺の型。
    private async Task<(Guid Source, Guid Target, Guid TypeId)> SeedPairAsync(
        string sourceConf = "internal", string targetConf = "internal")
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var type = EdgeType.Create($"w-{Guid.NewGuid():N}", EdgeTypeLayer.Core, false);
        await _factory.SeedAsync(db =>
        {
            db.EdgeTypes.Add(type);
            db.Documents.Add(Doc(source, sourceConf));
            db.Documents.Add(Doc(target, targetConf));
            return Task.CompletedTask;
        });
        return (source, target, type.Id);
    }

    private async Task<Guid> SeedSuggestionAsync(Guid source, Guid target, Guid typeId)
    {
        var s = AiSuggestion.CreateLink(source, target, typeId, "根拠", DateTimeOffset.UtcNow);
        await _factory.SeedAsync(db =>
        {
            db.AiSuggestions.Add(s);
            return Task.CompletedTask;
        });
        return s.Id;
    }

    private Task<HttpResponseMessage> PostEdgeAsync(Guid source, Guid target, Guid typeId)
        => _factory.CreateClient().PostAsJsonAsync("/graph/edges",
            new CreateGraphEdgeRequest(source, target, typeId), TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> PostAsync(string path)
        => _factory.CreateClient().PostAsync(path, null, TestContext.Current.CancellationToken);

    // FR-18, ADR-0063 決定 3, IADR-0361 決定 3 (#1187): 承認・却下の資格は「①write **または** ②管理者ロール」
    // の選言になった。**TestAuthHandler の既定ロールは `platform-admin`** なので、①の否定形を測るには
    // ②を落とさなければならない（落とさないと②で通り、否定形が空振りする）。
    private Task<HttpResponseMessage> PostAsNonAdminAsync(string path)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "viewer");
        return client.PostAsync(path, null, TestContext.Current.CancellationToken);
    }

    // ---- POST /graph/edges ----

    // 🔴 否定形: read は持つが write は持たない主体は、辺を作れない。
    [Fact]
    public async Task Read_only_subject_cannot_create_an_edge()
    {
        var (source, target, typeId) = await SeedPairAsync();
        _factory.ScopeProvider = _ => InternalOnly();
        _factory.WriteScopeProvider = _ => Denied();

        var res = await PostEdgeAsync(source, target, typeId);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "読み取り権限で書き込みが通ってはならない（#993）。403 にしないのは存在秘匿の一本道を守るため");
        // ⚠ DB はクラス内で共有される（IClassFixture）。**この試験が撒いた型に限って数える。**
        await ReadAsync(async db =>
            (await db.Edges.CountAsync(e => e.EdgeTypeId == typeId,
                TestContext.Current.CancellationToken)).Should().Be(0,
                "拒否した要求が辺を残してはならない"));
    }

    // 陽性対照: write スコープを持てば作れる（否定形が「常に 404」で緑になっていないこと）。
    [Fact]
    public async Task Positive_control_subject_with_write_scope_can_create_an_edge()
    {
        var (source, target, typeId) = await SeedPairAsync();
        _factory.ScopeProvider = _ => InternalOnly();
        _factory.WriteScopeProvider = _ => InternalOnly();

        var res = await PostEdgeAsync(source, target, typeId);

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await res.Content.ReadFromJsonAsync<GraphEdgeCreatedDto>(
            TestContext.Current.CancellationToken);
        dto!.Provenance.Should().Be(EdgeProvenance.User);
    }

    // 🔴 **解決したスコープを使っていることを測る。**
    // write スコープは Granted=true だが文書条件が合わない。`Granted` だけを見る実装なら緑にならない。
    [Fact]
    public async Task Write_scope_document_conditions_are_applied_not_just_granted()
    {
        var (source, target, typeId) = await SeedPairAsync();
        _factory.ScopeProvider = _ => InternalOnly();
        _factory.WriteScopeProvider = _ => PublicOnly();

        var res = await PostEdgeAsync(source, target, typeId);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "write スコープの文書条件（public 限定）に合わない起点へは張れない。"
          + "Granted だけを見る実装は、狭い write 権限で範囲外の文書を触れてしまう");
    }

    // 🔴 交差: write を持っていても、**read で見えない終点**へは張れない（ADR-0034 決定 8 が残っている）。
    [Fact]
    public async Task Write_scope_does_not_bypass_the_read_reachability_check()
    {
        var (source, target, typeId) = await SeedPairAsync(targetConf: "restricted");
        _factory.ScopeProvider = _ => InternalOnly();   // 終点 restricted は見えない
        _factory.WriteScopeProvider = _ => new AccessScopeResponse("test-user", [], true); // 無条件 write

        var res = await PostEdgeAsync(source, target, typeId);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "ADR-0034 決定 8 の具体化は『リンク先文書に対する作成者の閲覧権限を検証する』と定める。"
          + "write を足したことで read の検証が失われてはならない");
    }

    // ---- POST /graph/suggestions/{id}/approve ----

    // 🔴 否定形: 承認は辺を作る＝書き込みである（ADR-0033 決定 7）。**状態も遷移しない。**
    [Fact]
    public async Task Read_only_subject_cannot_approve_a_suggestion()
    {
        var (source, target, typeId) = await SeedPairAsync();
        var id = await SeedSuggestionAsync(source, target, typeId);
        _factory.ScopeProvider = _ => InternalOnly();
        _factory.WriteScopeProvider = _ => Denied();

        var res = await PostAsNonAdminAsync($"/graph/suggestions/{id}/approve");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await ReadAsync(async db =>
        {
            var s = await db.AiSuggestions.AsNoTracking()
                .FirstAsync(x => x.Id == id, TestContext.Current.CancellationToken);
            s.State.Should().Be(SuggestionState.Pending, "拒否した要求が状態を遷移させてはならない");
            (await db.Edges.CountAsync(e => e.EdgeTypeId == typeId,
                TestContext.Current.CancellationToken)).Should().Be(0);
        });
    }

    // 陽性対照: write スコープを持てば承認でき、辺が 1 本できる。
    [Fact]
    public async Task Positive_control_subject_with_write_scope_can_approve_a_suggestion()
    {
        var (source, target, typeId) = await SeedPairAsync();
        var id = await SeedSuggestionAsync(source, target, typeId);
        _factory.ScopeProvider = _ => InternalOnly();
        _factory.WriteScopeProvider = _ => InternalOnly();

        var res = await PostAsync($"/graph/suggestions/{id}/approve");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<AiSuggestionDto>(
            TestContext.Current.CancellationToken);
        dto!.State.Should().Be(SuggestionState.Approved);
        await ReadAsync(async db =>
            (await db.Edges.CountAsync(e => e.EdgeTypeId == typeId,
                TestContext.Current.CancellationToken)).Should().Be(1));
    }

    // ---- POST /graph/suggestions/{id}/reject ----

    // 🔴 否定形: 却下は共有された提案行を握り潰す操作である。read だけの主体には許さない。
    [Fact]
    public async Task Read_only_subject_cannot_reject_a_suggestion()
    {
        var (source, target, typeId) = await SeedPairAsync();
        var id = await SeedSuggestionAsync(source, target, typeId);
        _factory.ScopeProvider = _ => InternalOnly();
        _factory.WriteScopeProvider = _ => Denied();

        var res = await PostAsNonAdminAsync($"/graph/suggestions/{id}/reject");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await ReadAsync(async db =>
        {
            var s = await db.AiSuggestions.AsNoTracking()
                .FirstAsync(x => x.Id == id, TestContext.Current.CancellationToken);
            s.State.Should().Be(SuggestionState.Pending);
            s.RejectedCount.Should().Be(0, "拒否した要求が却下回数を増やしてはならない");
        });
    }

    // 陽性対照: write スコープを持てば却下できる。
    [Fact]
    public async Task Positive_control_subject_with_write_scope_can_reject_a_suggestion()
    {
        var (source, target, typeId) = await SeedPairAsync();
        var id = await SeedSuggestionAsync(source, target, typeId);
        _factory.ScopeProvider = _ => InternalOnly();
        _factory.WriteScopeProvider = _ => InternalOnly();

        var res = await PostAsync($"/graph/suggestions/{id}/reject");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<AiSuggestionDto>(
            TestContext.Current.CancellationToken);
        dto!.State.Should().Be(SuggestionState.Rejected);
    }

    // ---- 読み取り経路への非影響 ----

    // write スコープを deny にしても、読み取りは従来どおり通る（過剰適用していないこと）。
    [Fact]
    public async Task Read_paths_are_unaffected_by_a_denied_write_scope()
    {
        var (source, _, _) = await SeedPairAsync();
        _factory.ScopeProvider = _ => InternalOnly();
        _factory.WriteScopeProvider = _ => Denied();

        var node = await _factory.CreateClient()
            .GetAsync($"/graph/{source}", TestContext.Current.CancellationToken);
        var neighbors = await _factory.CreateClient()
            .GetAsync($"/graph/{source}/neighbors", TestContext.Current.CancellationToken);
        var suggestions = await _factory.CreateClient()
            .GetAsync("/graph/suggestions/", TestContext.Current.CancellationToken);

        node.StatusCode.Should().Be(HttpStatusCode.OK);
        neighbors.StatusCode.Should().Be(HttpStatusCode.OK);
        suggestions.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
