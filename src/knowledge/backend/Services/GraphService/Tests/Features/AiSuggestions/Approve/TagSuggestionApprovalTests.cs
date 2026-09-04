using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GraphService.Domain;
using GraphService.Domain.Ports;
using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Tests.Features.AiSuggestions.Approve;

// FR-18, SC-03, SC-05, SC-09, ADR-0033 決定 7・10, ADR-0063 決定 1〜4, IADR-0364 (#1187 / #1014):
// **タグ提案の承認 —— 反映・辞書の値域・認可の選言。**
//
// 🔴 **陰性は陽性対照と対で置く。** 「常に 404」「常に 400」「反映先を呼ばない」実装でも陰性だけなら
// 緑になる。本クラスは同じ経路で (a) write を持つ主体が反映まで成功する (b) 管理者ロールが
// 取り込み文書で成功する を陽性対照として持つ。
//
// 反映先（DocumentService）は `TestWebApplicationFactory.TagWriter`（記録スタブ）である。
// 実 HTTP の写像は `HttpDocumentTagWriterTests`、後段そのものは DocumentService.Tests が見る。
public class TagSuggestionApprovalTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public TagSuggestionApprovalTests(TestWebApplicationFactory factory) => _factory = factory;

    private static AccessScopeResponse InternalOnly()
        => new("test-user", [new AttributeFilter("confidentiality", ["internal"])], true);

    // write ポリシーが 1 件も無い＝所有者ベースでは書けない（取り込み文書 `owner=system` と同じ結果）。
    private static AccessScopeResponse Denied() => new("test-user", [], false);

    private static GraphDocument Doc(Guid id)
        => GraphDocument.Create(id, $"doc-{id:N}",
            new Dictionary<string, string> { ["confidentiality"] = "internal" },
            null, DateTimeOffset.UtcNow);

    private async Task<(Guid SuggestionId, Guid DocumentId)> SeedTagAsync(string tagValue)
    {
        var document = Guid.NewGuid();
        var s = AiSuggestion.CreateTag(document, tagValue, "根拠", DateTimeOffset.UtcNow);
        await _factory.SeedAsync(db =>
        {
            db.Documents.Add(Doc(document));
            db.AiSuggestions.Add(s);
            return Task.CompletedTask;
        });
        return (s.Id, document);
    }

    private HttpClient ClientWithRoles(string roles)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return client;
    }

    private Task<HttpResponseMessage> PostAsync(HttpClient client, string path)
        => client.PostAsync(path, null, TestContext.Current.CancellationToken);

    private async Task<string> StateOfAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GraphDbContext>();
        return (await db.AiSuggestions.AsNoTracking()
            .FirstAsync(s => s.Id == id, TestContext.Current.CancellationToken)).State;
    }

    // ---- 1187-1 / 1014-4: 反映（陽性対照） ----

    // 🔴 write を持つ主体が辞書内のタグ提案を承認すると、**反映先が（その文書・その値で）1 回呼ばれ**、
    // 状態が approved になる。
    [Fact]
    public async Task Approving_a_tag_suggestion_applies_the_tag_to_the_document_then_approves()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        _factory.WriteScopeProvider = _ => InternalOnly();
        _factory.TagWriter.Reset(TagWriteOutcome.Applied);
        var (id, document) = await SeedTagAsync("経理");

        var res = await PostAsync(ClientWithRoles("viewer"), $"/graph/suggestions/{id}/approve");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<AiSuggestionDto>(TestContext.Current.CancellationToken);
        dto!.State.Should().Be(SuggestionState.Approved);
        dto.CanDecide.Should().BeTrue();
        _factory.TagWriter.Calls.Should().ContainSingle()
            .Which.Should().Be((document, "経理"), "反映先は**その文書・その値**で 1 回だけ呼ばれる");
        (await StateOfAsync(id)).Should().Be(SuggestionState.Approved);
    }

    // 1187-2: 再承認は 409。**反映先は 2 度呼ばれない**（後段を呼ぶ前に状態を見る）。
    [Fact]
    public async Task Re_approving_is_a_conflict_and_does_not_call_the_document_service_again()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        _factory.WriteScopeProvider = _ => InternalOnly();
        _factory.TagWriter.Reset(TagWriteOutcome.Applied);
        var (id, _) = await SeedTagAsync("経理");
        var client = ClientWithRoles("viewer");
        (await PostAsync(client, $"/graph/suggestions/{id}/approve")).StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await PostAsync(client, $"/graph/suggestions/{id}/approve");

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _factory.TagWriter.Calls.Should().HaveCount(1, "409 は後段を呼ぶ前に決まる");
    }

    // ---- 1187-7 / 1014-2・3: 辞書の値域（承認段） ----

    // 🔴 後段が「辞書に無い」と応えたら **400 `unknown_tag`**。状態は pending のまま（承認できず却下のみ。
    // ADR-0063 決定 2 後段）。`unknown_edge_type` と同じ形である。
    [Fact]
    public async Task Unknown_tag_is_rejected_with_400_and_the_suggestion_stays_pending()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        _factory.WriteScopeProvider = _ => InternalOnly();
        _factory.TagWriter.Reset(TagWriteOutcome.UnknownTag);
        var (id, _) = await SeedTagAsync("辞書に無い");

        var res = await PostAsync(ClientWithRoles("viewer"), $"/graph/suggestions/{id}/approve");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("unknown_tag");
        (await StateOfAsync(id)).Should().Be(SuggestionState.Pending,
            "反映できていない提案を承認済みにしてはならない");
    }

    // 辞書外の提案は**却下だけができる**（決定 2 後段の後半。陽性対照: 同じ提案が却下では通る）。
    [Fact]
    public async Task Unknown_tag_suggestion_can_still_be_rejected()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        _factory.WriteScopeProvider = _ => InternalOnly();
        _factory.TagWriter.Reset(TagWriteOutcome.UnknownTag);
        var (id, _) = await SeedTagAsync("辞書に無い");

        var res = await PostAsync(ClientWithRoles("viewer"), $"/graph/suggestions/{id}/reject");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StateOfAsync(id)).Should().Be(SuggestionState.Rejected);
        _factory.TagWriter.Calls.Should().BeEmpty("却下は反映先を呼ばない");
    }

    // ---- 後段の失敗は成功へ縮退しない ----

    // 後段へ到達できない → **502**。状態は pending のまま。
    [Fact]
    public async Task Unreachable_document_service_yields_502_and_no_transition()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        _factory.WriteScopeProvider = _ => InternalOnly();
        _factory.TagWriter.Reset(TagWriteOutcome.Unavailable);
        var (id, _) = await SeedTagAsync("経理");

        var res = await PostAsync(ClientWithRoles("viewer"), $"/graph/suggestions/{id}/approve");

        res.StatusCode.Should().Be(HttpStatusCode.BadGateway,
            "承認できていないのに承認済みと見えるのが最悪である");
        (await StateOfAsync(id)).Should().Be(SuggestionState.Pending);
    }

    // 後段（最終防衛線）が拒んだ → 404 の一本道。状態は pending のまま。
    [Fact]
    public async Task Document_service_refusal_is_a_not_found_and_no_transition()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        _factory.WriteScopeProvider = _ => InternalOnly();
        _factory.TagWriter.Reset(TagWriteOutcome.NotWritable);
        var (id, _) = await SeedTagAsync("経理");

        var res = await PostAsync(ClientWithRoles("viewer"), $"/graph/suggestions/{id}/approve");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await StateOfAsync(id)).Should().Be(SuggestionState.Pending);
    }

    // ---- 1187-3〜6: 認可の選言（①write または ②管理者ロール） ----

    // 🔴 1187-3 陽性対照（②）: write を持たない（＝取り込み文書 `owner=system` と同じ）主体でも、
    // **管理者ロールなら承認できる**。ADR-0063 決定 3 の中心 —— ②が無いとこの 1 件が通らない。
    [Fact]
    public async Task Admin_can_approve_a_tag_suggestion_on_a_document_nobody_can_write()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        _factory.WriteScopeProvider = _ => Denied();
        _factory.TagWriter.Reset(TagWriteOutcome.Applied);
        var (id, document) = await SeedTagAsync("経理");

        var res = await PostAsync(ClientWithRoles("platform-admin"), $"/graph/suggestions/{id}/approve");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.TagWriter.Calls.Should().ContainSingle().Which.DocumentId.Should().Be(document);
        (await StateOfAsync(id)).Should().Be(SuggestionState.Approved);
    }

    // 🔴 1187-4: write もロールも無い主体は 404（存在秘匿。403 にしない）。**反映先を呼ばない。**
    [Fact]
    public async Task Subject_without_write_or_admin_role_cannot_approve()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        _factory.WriteScopeProvider = _ => Denied();
        _factory.TagWriter.Reset(TagWriteOutcome.Applied);
        var (id, _) = await SeedTagAsync("経理");

        var res = await PostAsync(ClientWithRoles("viewer"), $"/graph/suggestions/{id}/approve");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _factory.TagWriter.Calls.Should().BeEmpty("拒んだ要求が後段へ書きに行ってはならない");
        (await StateOfAsync(id)).Should().Be(SuggestionState.Pending);
    }

    // 🔴 1187-5: 同じ主体は却下もできない（決定 4。承認と却下は同じ権限に従う）。
    [Fact]
    public async Task Subject_without_write_or_admin_role_cannot_reject_either()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        _factory.WriteScopeProvider = _ => Denied();
        var (id, _) = await SeedTagAsync("経理");

        var res = await PostAsync(ClientWithRoles("viewer"), $"/graph/suggestions/{id}/reject");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await StateOfAsync(id)).Should().Be(SuggestionState.Pending);
    }

    // 1187-6: 管理者ロールは却下もできる（②が却下にも効く）。
    [Fact]
    public async Task Admin_can_reject_a_tag_suggestion_on_a_document_nobody_can_write()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        _factory.WriteScopeProvider = _ => Denied();
        var (id, _) = await SeedTagAsync("経理");

        var res = await PostAsync(ClientWithRoles("platform-admin"), $"/graph/suggestions/{id}/reject");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StateOfAsync(id)).Should().Be(SuggestionState.Rejected);
    }

    // ②は **`platform-admin` だけ**である。運用者は SC-05 の編集経路を持たない。
    [Fact]
    public async Task Operator_role_alone_does_not_qualify()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        _factory.WriteScopeProvider = _ => Denied();
        _factory.TagWriter.Reset(TagWriteOutcome.Applied);
        var (id, _) = await SeedTagAsync("経理");

        var res = await PostAsync(ClientWithRoles("platform-operator"), $"/graph/suggestions/{id}/approve");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _factory.TagWriter.Calls.Should().BeEmpty();
    }

    // ②でも**可視性は先に立つ**: 端点が見えない提案は管理者でも 404（ADR-0034 決定 8）。
    [Fact]
    public async Task Admin_role_does_not_bypass_the_visibility_check()
    {
        _factory.ScopeProvider = _ => new AccessScopeResponse("test-user",
            [new AttributeFilter("confidentiality", ["public"])], true);   // internal は見えない
        _factory.WriteScopeProvider = _ => InternalOnly();
        _factory.TagWriter.Reset(TagWriteOutcome.Applied);
        var (id, _) = await SeedTagAsync("経理");

        var res = await PostAsync(ClientWithRoles("platform-admin"), $"/graph/suggestions/{id}/approve");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _factory.TagWriter.Calls.Should().BeEmpty();
    }

    // ---- 1187-8・9 の土台: 一覧が行ごとに `canDecide` を運ぶ（決定 5 の判定はサーバ側） ----

    [Fact]
    public async Task Listing_carries_can_decide_per_row_according_to_write_scope_and_role()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        _factory.WriteScopeProvider = _ => Denied();
        var (id, document) = await SeedTagAsync("経理");

        var asViewer = await ListAsync(ClientWithRoles("viewer"), document);
        var asAdmin = await ListAsync(ClientWithRoles("platform-admin"), document);

        asViewer.Single(i => i.Id == id).CanDecide.Should().BeFalse("write も管理者ロールも無い");
        asAdmin.Single(i => i.Id == id).CanDecide.Should().BeTrue("陽性対照: 管理者ロールで資格がある");

        // ① の側: write スコープを持てば、ロールが無くても資格がある。
        _factory.WriteScopeProvider = _ => InternalOnly();
        var asWriter = await ListAsync(ClientWithRoles("viewer"), document);
        asWriter.Single(i => i.Id == id).CanDecide.Should().BeTrue("陽性対照: write スコープで資格がある");
    }

    private async Task<List<AiSuggestionDto>> ListAsync(HttpClient client, Guid document)
    {
        var res = await client.GetAsync($"/graph/suggestions/?documentId={document}",
            TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<List<AiSuggestionDto>>(
            TestContext.Current.CancellationToken))!;
    }
}
