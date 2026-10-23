using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain.Ports;
using DocumentService.Features.Documents;
using DocumentService.Features.Documents.GetContent;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.BreakGlass;

// FR-19, UC-11, SC-03, SC-19, 計画 ADR-0036 D-08, 08_data-egress-policy §経路 1, [[IADR-0610]] (#2046):
// **緊急アクセスで開くのは「指名された管理者 × 対象の 1 件 × 有効な間 × 閲覧だけ」である**ことを固定する
// （planning#841 の裁定 2026-10-23）。
//
// 🔴 陰性（404）は必ず陽性対照（同じ時点・同じ資料で指名された管理者には 200）と対にする。「常に 404」の実装でも
//   陰性だけは緑になるため。時刻は偽の時計で進める（壁時計を待たない）。
[Trait("TestKind", "Integration")]
public class BreakGlassViewAuthorizationTests(BreakGlassWebApplicationFactory factory)
    : IClassFixture<BreakGlassWebApplicationFactory>
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient ClientAs(string user, params string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader,
            roles.Length > 0 ? string.Join(",", roles) : "viewer");
        return client;
    }

    private sealed record Scenario(string Owner, string Nominee, Guid DocumentId, BreakGlassGrantDto Grant);

    // 所有者が自分の資料 1 件について、管理者 1 人を指名して発動する（陽性の前提）。
    private async Task<Scenario> ActivateAsync()
    {
        var owner = BreakGlassSeed.Unique("owner");
        var nominee = BreakGlassSeed.Unique("admin");
        factory.AdminDirectory.States[nominee] = ApproverAdminState.Admin;
        var doc = await BreakGlassSeed.PrivateNoteAsync(factory, owner, Ct);

        var res = await ClientAs(owner).PostAsJsonAsync($"/private-notes/{doc.Id}/break-glass",
            new ActivateBreakGlassRequest("端末を紛失したため管理者に確認を依頼する", nominee), Ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync(Ct));
        var grant = (await res.Content.ReadFromJsonAsync<BreakGlassGrantDto>(Ct))!;
        return new Scenario(owner, nominee, doc.Id, grant);
    }

    private Task<HttpResponseMessage> ViewAsync(string user, Guid id)
        => ClientAs(user, "platform-admin").GetAsync($"/documents/{id}", Ct);

    private async Task<List<string>> ViewAuditTargetsAsync(Guid documentId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        return await db.BreakGlassAuditEntries
            .Where(a => a.DocumentId == documentId && a.Operation == BreakGlassOperations.View)
            .OrderBy(a => a.OccurredAt).Select(a => a.ViewTarget!).ToListAsync(Ct);
    }

    // ── 陽性: 指名された管理者は有効な間だけ対象の 1 件を閲覧でき、閲覧は 1 件ずつ記録される ──

    [Fact]
    public async Task 指名された管理者は有効な間に対象を閲覧でき_閲覧は1件ずつ監査ログに残る()
    {
        var s = await ActivateAsync();

        var first = await ViewAsync(s.Nominee, s.DocumentId);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await first.Content.ReadFromJsonAsync<DocumentDto>(Ct))!;
        dto.Id.Should().Be(s.DocumentId);
        dto.SharedWith.Should().BeNull("緊急アクセスの応答に共有先の写しを載せない");
        (await ViewAsync(s.Nominee, s.DocumentId)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ViewAuditTargetsAsync(s.DocumentId)).Should().Equal(
            [BreakGlassViewTargets.Document, BreakGlassViewTargets.Document], "閲覧は 1 件ずつ記録する（裁定 5）");
    }

    [Fact]
    public async Task 発動していなければ管理者でも閲覧できない()
    {
        var owner = BreakGlassSeed.Unique("owner");
        var admin = BreakGlassSeed.Unique("admin");
        factory.AdminDirectory.States[admin] = ApproverAdminState.Admin;
        var doc = await BreakGlassSeed.PrivateNoteAsync(factory, owner, Ct);

        (await ViewAsync(admin, doc.Id)).StatusCode.Should().Be(HttpStatusCode.NotFound, "平時は管理者も読めない（D-08）");
        (await ClientAs(owner).GetAsync($"/documents/{doc.Id}", Ct)).StatusCode
            .Should().Be(HttpStatusCode.OK, "陽性対照: 所有者には返る");
        (await ViewAuditTargetsAsync(doc.Id)).Should().BeEmpty();
    }

    // ── 指名外の管理者 ──

    [Fact]
    public async Task 指名されていない管理者は有効中でも閲覧できない()
    {
        var s = await ActivateAsync();
        var other = BreakGlassSeed.Unique("admin2");
        factory.AdminDirectory.States[other] = ApproverAdminState.Admin;

        (await ViewAsync(other, s.DocumentId)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "platform-admin 全員には開かない（裁定 2）");
        (await ViewAsync(s.Nominee, s.DocumentId)).StatusCode.Should().Be(HttpStatusCode.OK, "陽性対照");
        (await ViewAuditTargetsAsync(s.DocumentId)).Should().HaveCount(1, "拒んだ閲覧は記録しない");
    }

    [Theory]
    [InlineData(ApproverAdminState.NotAdmin)]
    [InlineData(ApproverAdminState.Unknown)]
    public async Task 指名された人がいま管理者でない_または確かめられないなら閲覧できない(ApproverAdminState state)
    {
        var s = await ActivateAsync();
        (await ViewAsync(s.Nominee, s.DocumentId)).StatusCode.Should().Be(HttpStatusCode.OK, "陽性対照");

        factory.AdminDirectory.States[s.Nominee] = state;

        (await ViewAsync(s.Nominee, s.DocumentId)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "ロールは名簿でいま確かめる。分からないは許さない（fail-closed）");
    }

    // ── 時間: 15 分で失効し、延長しない ──

    [Fact]
    public async Task 発動から15分で失効し_直前までは閲覧できる()
    {
        var s = await ActivateAsync();

        factory.Clock.Advance(TimeSpan.FromMinutes(BreakGlassPolicy.ValidityMinutes) - TimeSpan.FromSeconds(1));
        (await ViewAsync(s.Nominee, s.DocumentId)).StatusCode.Should().Be(HttpStatusCode.OK, "14 分 59 秒は有効");

        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        (await ViewAsync(s.Nominee, s.DocumentId)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "発動＋15 分ちょうどで失効する");
    }

    [Fact]
    public async Task 閲覧を続けても有効時間は延びない()
    {
        var s = await ActivateAsync();
        for (var i = 0; i < 3; i++)
        {
            factory.Clock.Advance(TimeSpan.FromMinutes(4));
            (await ViewAsync(s.Nominee, s.DocumentId)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        factory.Clock.Advance(TimeSpan.FromMinutes(3)); // 発動から 15 分
        (await ViewAsync(s.Nominee, s.DocumentId)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "アイドルタイムアウトにしない（起点は発動時刻）");
    }

    // ── 本人の早期終了 ──

    [Fact]
    public async Task 本人が終えた後は閲覧できない()
    {
        var s = await ActivateAsync();
        (await ViewAsync(s.Nominee, s.DocumentId)).StatusCode.Should().Be(HttpStatusCode.OK, "陽性対照");

        factory.Clock.Advance(TimeSpan.FromMinutes(2));
        (await ClientAs(s.Owner).PostAsync($"/private-notes/{s.DocumentId}/break-glass/end", null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await ViewAsync(s.Nominee, s.DocumentId)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── 対象は 1 件だけ ──

    [Fact]
    public async Task 同じ所有者の別の資料は閲覧できない()
    {
        var s = await ActivateAsync();
        var sibling = await BreakGlassSeed.PrivateNoteAsync(factory, s.Owner, Ct);

        (await ViewAsync(s.Nominee, sibling.Id)).StatusCode.Should().Be(HttpStatusCode.NotFound, "資料 1 件ごとに発動する");
        (await ViewAsync(s.Nominee, s.DocumentId)).StatusCode.Should().Be(HttpStatusCode.OK, "陽性対照");
    }

    // ── 閲覧以外の読み取りの口（一覧・版・MCP の経路）では開かない ──

    [Fact]
    public async Task 一覧と版の口では有効中でも開かない()
    {
        var s = await ActivateAsync();
        var admin = ClientAs(s.Nominee, "platform-admin");

        var list = (await admin.GetFromJsonAsync<List<DocumentDto>>("/documents", Ct))!;
        list.Should().NotContain(d => d.Id == s.DocumentId, "一覧は描かない（SC-19）");
        (await admin.GetAsync($"/documents/{s.DocumentId}/versions", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.GetAsync($"/documents/{s.DocumentId}/versions/1", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.GetAsync($"/documents/{s.DocumentId}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK, "陽性対照");
    }

    [Fact]
    public async Task 閲覧の口以外の取得の関数_MCPのツール実行が使う形_では開かない()
    {
        var s = await ActivateAsync();
        using var scope = factory.Services.CreateScope();
        var reads = scope.ServiceProvider.GetRequiredService<DocumentReadUseCase>();
        var relayed = DocumentReadPrincipal.RelayedUser(s.Nominee);

        (await reads.GetAsync(relayed, s.DocumentId, Ct)).Should().BeNull("GetAsync は緊急アクセスを含まない");
        (await reads.GetForViewAsync(relayed, s.DocumentId, BreakGlassViewTargets.Document, Ct))
            .Should().NotBeNull("陽性対照: gRPC で中継された同じ管理者は SC-03 の閲覧の口では読める");
    }

    [Fact]
    public async Task 本文の閲覧も有効中だけ許し_本文として記録する()
    {
        var s = await ActivateAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var contents = scope.ServiceProvider.GetRequiredService<DocumentContentUseCase>();
            (await contents.ReadAsync(DocumentReadPrincipal.RelayedUser(s.Nominee), s.DocumentId, Ct))
                .Should().NotBeNull();
        }

        (await ViewAuditTargetsAsync(s.DocumentId)).Should().Equal([BreakGlassViewTargets.Content]);

        factory.Clock.Advance(TimeSpan.FromMinutes(BreakGlassPolicy.ValidityMinutes));
        using (var scope = factory.Services.CreateScope())
        {
            var contents = scope.ServiceProvider.GetRequiredService<DocumentContentUseCase>();
            (await contents.ReadAsync(DocumentReadPrincipal.RelayedUser(s.Nominee), s.DocumentId, Ct))
                .Should().BeNull("失効後は本文も読めない");
        }
    }

    [Fact]
    public async Task 機械の主体は指名された名前を名乗っても開かない()
    {
        var s = await ActivateAsync();
        using var scope = factory.Services.CreateScope();
        var reads = scope.ServiceProvider.GetRequiredService<DocumentReadUseCase>();

        (await reads.GetForViewAsync(DocumentReadPrincipal.RelayedUser("service-account-" + s.Nominee), s.DocumentId,
            BreakGlassViewTargets.Document, Ct)).Should().BeNull();
    }

    // ── 論理削除中は開かない ──

    [Fact]
    public async Task 論理削除した資料は有効中でも閲覧できない()
    {
        var s = await ActivateAsync();
        (await ClientAs(s.Owner).DeleteAsync($"/private-notes/{s.DocumentId}", Ct)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        (await ViewAsync(s.Nominee, s.DocumentId)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "論理削除の間は共有先も開けない（ADR-0142）。緊急アクセスも同じ向き");
    }

    // ── 閲覧以外の操作（編集・削除・共有の変更・本文の投入・露出・復元・完全削除）は増えない ──

    public static TheoryData<string> WriteOperations() =>
    [
        "PUT", "PATCH", "PUBLISH", "ARCHIVE", "DELETE", "BODY", "SHARE", "UNSHARE",
        "SOFT_DELETE", "EXPOSURE", "RESTORE", "PURGE",
    ];

    [Theory]
    [MemberData(nameof(WriteOperations))]
    public async Task 閲覧以外の操作は有効中の指名された管理者にも許さない(string operation)
    {
        var s = await ActivateAsync();
        var admin = ClientAs(s.Nominee, "platform-admin");
        (await admin.GetAsync($"/documents/{s.DocumentId}", Ct)).StatusCode
            .Should().Be(HttpStatusCode.OK, "陽性対照: 有効中で閲覧はできる");

        Dictionary<string, string> takeover = new()
        {
            ["confidentiality"] = "restricted",
            ["doc_scope"] = "private-note",
            ["owner"] = s.Nominee,
        };
        var id = s.DocumentId;
        var res = operation switch
        {
            "PUT" => await admin.PutAsJsonAsync($"/documents/{id}",
                new { title = "書き換え", attributes = takeover, tags = new List<string>() }, Ct),
            "PATCH" => await admin.PatchAsJsonAsync($"/documents/{id}/metadata",
                new { attributes = takeover, tags = new List<string>() }, Ct),
            "PUBLISH" => await admin.PostAsync($"/documents/{id}/publish", null, Ct),
            "ARCHIVE" => await admin.PostAsync($"/documents/{id}/archive", null, Ct),
            "DELETE" => await admin.DeleteAsync($"/documents/{id}", Ct),
            "BODY" => await admin.PutAsJsonAsync($"/documents/{id}/body", new { body = "# 管理者" }, Ct),
            "SHARE" => await admin.PostAsJsonAsync($"/documents/{id}/shares",
                new CreateShareRequest("user", BreakGlassSeed.Unique("x")), Ct),
            "UNSHARE" => await admin.DeleteAsync($"/documents/{id}/shares/user/{s.Nominee}", Ct),
            "SOFT_DELETE" => await admin.DeleteAsync($"/private-notes/{id}", Ct),
            "EXPOSURE" => await admin.PutAsJsonAsync($"/private-notes/{id}/exposure",
                new UpdateExposureRequest(true, true, true), Ct),
            "RESTORE" => await admin.PostAsync($"/private-notes/{id}/restore", null, Ct),
            "PURGE" => await admin.PostAsJsonAsync("/private-notes/purge", new PurgePrivateNotesRequest([id]), Ct),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        ((int)res.StatusCode).Should().BeGreaterThanOrEqualTo(400, $"{operation} は閲覧ではない（権限は閲覧だけ）");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var doc = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == id, Ct);
        doc.Attributes["owner"].Should().Be(s.Owner);
        doc.Version.Should().Be(1, "版が進んでいない");
        var note = await db.PrivateNotes.AsNoTracking().SingleAsync(n => n.DocumentId == id, Ct);
        note.IsDeleted.Should().BeFalse();
        (note.IncludeInSearch || note.IncludeInGraph || note.IncludeInAi).Should().BeFalse();
        (await db.DocumentShares.AnyAsync(sh => sh.DocumentId == id, Ct)).Should().BeFalse();
    }
}
