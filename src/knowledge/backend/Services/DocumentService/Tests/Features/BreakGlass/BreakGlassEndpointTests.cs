using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Features.PrivateNotes.Maintenance;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.BreakGlass;

// FR-19, UC-11, SC-19, 計画 ADR-0036 D-08, 08_data-egress-policy §経路 1, [[IADR-0610]] (#2046):
// 発動・終了・状態・監査ログの読み取りの口（planning#841 の裁定 2026-10-23）。
//   - 発動・終了・状態は**所有者本人だけ**（管理者を含む第三者の発動経路が無い）
//   - 理由は必須、指名先は platform-admin の 1 人
//   - 監査ログは platform-admin（全件）と本人（自分の資料の行だけ）。本文・題名を返さない。保持は 3 年
[Trait("TestKind", "Integration")]
public class BreakGlassEndpointTests(BreakGlassWebApplicationFactory factory)
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

    private string NewAdmin(ApproverAdminState state = ApproverAdminState.Admin)
    {
        var admin = BreakGlassSeed.Unique("admin");
        factory.AdminDirectory.States[admin] = state;
        return admin;
    }

    private Task<HttpResponseMessage> ActivateAsync(HttpClient client, Guid id, string? reason, string? nominee)
        => client.PostAsJsonAsync($"/private-notes/{id}/break-glass", new ActivateBreakGlassRequest(reason, nominee), Ct);

    private async Task<int> GrantCountAsync(Guid documentId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        return await db.BreakGlassGrants.CountAsync(g => g.DocumentId == documentId, Ct);
    }

    // ── 発動: 本人だけ ──

    [Fact]
    public async Task 本人は理由と指名で発動でき_15分の発動記録と発動の監査ログが残る()
    {
        var owner = BreakGlassSeed.Unique("owner");
        var admin = NewAdmin();
        var doc = await BreakGlassSeed.PrivateNoteAsync(factory, owner, Ct);
        var now = factory.Clock.GetUtcNow();

        var res = await ActivateAsync(ClientAs(owner), doc.Id, "  端末紛失  ", admin);

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var grant = (await res.Content.ReadFromJsonAsync<BreakGlassGrantDto>(Ct))!;
        grant.DocumentId.Should().Be(doc.Id);
        grant.NomineeUserId.Should().Be(admin);
        grant.Reason.Should().Be("端末紛失");
        grant.ActivatedAt.Should().Be(now);
        grant.ExpiresAt.Should().Be(now.AddMinutes(15));
        grant.EndsAt.Should().Be(now.AddMinutes(15));
        grant.Active.Should().BeTrue();

        var audit = (await ClientAs(owner).GetFromJsonAsync<List<BreakGlassAuditEntryDto>>(
            $"/break-glass/audit?documentId={doc.Id}", Ct))!;
        audit.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Operation = BreakGlassOperations.Activate,
            ActorUserId = owner,
            OwnerUserId = owner,
            NomineeUserId = admin,
            DocumentId = doc.Id,
            Reason = "端末紛失",
            ActivatedAt = now,
            EndsAt = now.AddMinutes(15),
        });
    }

    [Fact]
    public async Task 管理者を含む第三者は他人の資料に発動できない()
    {
        var owner = BreakGlassSeed.Unique("owner");
        var admin = NewAdmin();
        var doc = await BreakGlassSeed.PrivateNoteAsync(factory, owner, Ct);

        (await ActivateAsync(ClientAs(admin, "platform-admin"), doc.Id, "理由", admin)).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "管理者でも本人でなければ発動できない（存在ごと秘匿）");
        (await ActivateAsync(ClientAs(BreakGlassSeed.Unique("other")), doc.Id, "理由", admin)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await GrantCountAsync(doc.Id)).Should().Be(0);

        (await ActivateAsync(ClientAs(owner), doc.Id, "理由", admin)).StatusCode
            .Should().Be(HttpStatusCode.Created, "陽性対照: 本人は発動できる");
    }

    [Fact]
    public async Task 機械の主体は所有者の名前でも発動できない()
    {
        var machine = "service-account-" + BreakGlassSeed.Unique("svc");
        var doc = await BreakGlassSeed.PrivateNoteAsync(factory, machine, Ct);

        (await ActivateAsync(ClientAs(machine), doc.Id, "理由", NewAdmin())).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await GrantCountAsync(doc.Id)).Should().Be(0);
    }

    [Fact]
    public async Task 組織文書には発動できない()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var owner = BreakGlassSeed.Unique("owner");
        var org = Document.Create("org", null, null, new Dictionary<string, string>
        {
            ["doc_scope"] = "organization",
            ["owner"] = owner,
            ["confidentiality"] = "internal",
        });
        db.Documents.Add(org);
        await db.SaveChangesAsync(Ct);

        (await ActivateAsync(ClientAs(owner), org.Id, "理由", NewAdmin())).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    // ── 入力の検証 ──

    [Theory]
    [InlineData(null, "reason")]
    [InlineData("", "reason")]
    [InlineData("   ", "reason")]
    public async Task 理由が空なら発動しない(string? reason, string key)
    {
        var owner = BreakGlassSeed.Unique("owner");
        var doc = await BreakGlassSeed.PrivateNoteAsync(factory, owner, Ct);

        var res = await ActivateAsync(ClientAs(owner), doc.Id, reason, NewAdmin());

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain(key);
        (await GrantCountAsync(doc.Id)).Should().Be(0);
    }

    [Fact]
    public async Task 理由が長すぎれば発動しない()
    {
        var owner = BreakGlassSeed.Unique("owner");
        var doc = await BreakGlassSeed.PrivateNoteAsync(factory, owner, Ct);

        (await ActivateAsync(ClientAs(owner), doc.Id, new string('あ', BreakGlassPolicy.ReasonMaxLength + 1), NewAdmin()))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ActivateAsync(ClientAs(owner), doc.Id, new string('あ', BreakGlassPolicy.ReasonMaxLength), NewAdmin()))
            .StatusCode.Should().Be(HttpStatusCode.Created, "陽性対照: 上限ちょうどは通る");
    }

    [Fact]
    public async Task 指名先が空_本人_サービスアカウント_管理者でないなら発動しない()
    {
        var owner = BreakGlassSeed.Unique("owner");
        var doc = await BreakGlassSeed.PrivateNoteAsync(factory, owner, Ct);
        factory.AdminDirectory.States[owner] = ApproverAdminState.Admin;
        var svc = "service-account-" + BreakGlassSeed.Unique("svc");
        factory.AdminDirectory.States[svc] = ApproverAdminState.Admin;

        foreach (var nominee in new[] { null, " ", owner, svc, NewAdmin(ApproverAdminState.NotAdmin) })
        {
            var res = await ActivateAsync(ClientAs(owner), doc.Id, "理由", nominee);
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"指名先 '{nominee}'");
            (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("nomineeUserId");
        }
        (await GrantCountAsync(doc.Id)).Should().Be(0);
    }

    [Fact]
    public async Task 指名先が管理者か確かめられなければ503で発動しない()
    {
        var owner = BreakGlassSeed.Unique("owner");
        var doc = await BreakGlassSeed.PrivateNoteAsync(factory, owner, Ct);

        (await ActivateAsync(ClientAs(owner), doc.Id, "理由", NewAdmin(ApproverAdminState.Unknown))).StatusCode
            .Should().Be(HttpStatusCode.ServiceUnavailable);
        (await GrantCountAsync(doc.Id)).Should().Be(0);
    }

    [Fact]
    public async Task 有効な発動がある間は重ねて発動できない_終えれば再び発動できる()
    {
        var owner = BreakGlassSeed.Unique("owner");
        var doc = await BreakGlassSeed.PrivateNoteAsync(factory, owner, Ct);
        var client = ClientAs(owner);

        (await ActivateAsync(client, doc.Id, "理由", NewAdmin())).StatusCode.Should().Be(HttpStatusCode.Created);
        (await ActivateAsync(client, doc.Id, "理由", NewAdmin())).StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await client.PostAsync($"/private-notes/{doc.Id}/break-glass/end", null, Ct)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await ActivateAsync(client, doc.Id, "理由", NewAdmin())).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task 論理削除中の資料には発動できない()
    {
        var owner = BreakGlassSeed.Unique("owner");
        var doc = await BreakGlassSeed.PrivateNoteAsync(factory, owner, Ct);
        (await ClientAs(owner).DeleteAsync($"/private-notes/{doc.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ActivateAsync(ClientAs(owner), doc.Id, "理由", NewAdmin())).StatusCode
            .Should().Be(HttpStatusCode.Conflict);
        (await GrantCountAsync(doc.Id)).Should().Be(0);
    }

    // ── 終了・状態: 本人だけ ──

    [Fact]
    public async Task 本人は15分より前に終えられ_終了日時は終えた時刻になる()
    {
        var owner = BreakGlassSeed.Unique("owner");
        var doc = await BreakGlassSeed.PrivateNoteAsync(factory, owner, Ct);
        var client = ClientAs(owner);
        await ActivateAsync(client, doc.Id, "理由", NewAdmin());
        factory.Clock.Advance(TimeSpan.FromMinutes(5));
        var endedAt = factory.Clock.GetUtcNow();

        var res = await client.PostAsync($"/private-notes/{doc.Id}/break-glass/end", null, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var grant = (await res.Content.ReadFromJsonAsync<BreakGlassGrantDto>(Ct))!;
        grant.EndedAt.Should().Be(endedAt);
        grant.EndsAt.Should().Be(endedAt, "終了日時は本人の終了か発動＋15 分の早いほう");
        grant.Active.Should().BeFalse();

        var status = (await client.GetFromJsonAsync<BreakGlassStatusResponse>($"/private-notes/{doc.Id}/break-glass", Ct))!;
        status.Active.Should().BeNull();

        var audit = (await client.GetFromJsonAsync<List<BreakGlassAuditEntryDto>>(
            $"/break-glass/audit?documentId={doc.Id}", Ct))!;
        audit.Select(a => a.Operation).Should().Equal(BreakGlassOperations.End, BreakGlassOperations.Activate);
        audit.Should().OnlyContain(a => a.EndsAt == endedAt, "発動の行も発動記録の現在の終了日時で返す");

        (await client.PostAsync($"/private-notes/{doc.Id}/break-glass/end", null, Ct)).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "終えたものはもう終えられない");
    }

    [Fact]
    public async Task 失効した発動は終えられず_状態は無効になる()
    {
        var owner = BreakGlassSeed.Unique("owner");
        var doc = await BreakGlassSeed.PrivateNoteAsync(factory, owner, Ct);
        var client = ClientAs(owner);
        await ActivateAsync(client, doc.Id, "理由", NewAdmin());

        (await client.GetFromJsonAsync<BreakGlassStatusResponse>($"/private-notes/{doc.Id}/break-glass", Ct))!
            .Active.Should().NotBeNull("陽性対照: 有効な間は状態に出る");
        factory.Clock.Advance(TimeSpan.FromMinutes(15));

        (await client.GetFromJsonAsync<BreakGlassStatusResponse>($"/private-notes/{doc.Id}/break-glass", Ct))!
            .Active.Should().BeNull();
        (await client.PostAsync($"/private-notes/{doc.Id}/break-glass/end", null, Ct)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task 指名された管理者を含む第三者は終了も状態の照会もできない()
    {
        var owner = BreakGlassSeed.Unique("owner");
        var admin = NewAdmin();
        var doc = await BreakGlassSeed.PrivateNoteAsync(factory, owner, Ct);
        await ActivateAsync(ClientAs(owner), doc.Id, "理由", admin);

        var adminClient = ClientAs(admin, "platform-admin");
        (await adminClient.PostAsync($"/private-notes/{doc.Id}/break-glass/end", null, Ct)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await adminClient.GetAsync($"/private-notes/{doc.Id}/break-glass", Ct)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        (await ClientAs(owner).GetFromJsonAsync<BreakGlassStatusResponse>($"/private-notes/{doc.Id}/break-glass", Ct))!
            .Active.Should().NotBeNull("第三者の終了は効いていない");
    }

    // ── 監査ログの読み取り: platform-admin と本人（自分の資料の分だけ） ──

    [Fact]
    public async Task 本人は自分の資料の行だけを読め_管理者は全件を読める()
    {
        var alice = BreakGlassSeed.Unique("alice");
        var bob = BreakGlassSeed.Unique("bob");
        var aliceDoc = await BreakGlassSeed.PrivateNoteAsync(factory, alice, Ct);
        var bobDoc = await BreakGlassSeed.PrivateNoteAsync(factory, bob, Ct);
        await ActivateAsync(ClientAs(alice), aliceDoc.Id, "理由 A", NewAdmin());
        await ActivateAsync(ClientAs(bob), bobDoc.Id, "理由 B", NewAdmin());

        var aliceRows = (await ClientAs(alice).GetFromJsonAsync<List<BreakGlassAuditEntryDto>>(
            "/break-glass/audit?limit=200", Ct))!;
        aliceRows.Should().NotBeEmpty().And.OnlyContain(a => a.OwnerUserId == alice);
        (await ClientAs(alice).GetFromJsonAsync<List<BreakGlassAuditEntryDto>>(
            $"/break-glass/audit?documentId={bobDoc.Id}", Ct))!.Should().BeEmpty("他人の資料の行は ID を指定しても読めない");

        var adminRows = (await ClientAs(BreakGlassSeed.Unique("auditor"), "platform-admin")
            .GetFromJsonAsync<List<BreakGlassAuditEntryDto>>("/break-glass/audit?limit=200", Ct))!;
        adminRows.Should().Contain(a => a.DocumentId == aliceDoc.Id).And.Contain(a => a.DocumentId == bobDoc.Id);
    }

    [Fact]
    public async Task 機械の主体は監査ログを読めない()
    {
        var res = await ClientAs("service-account-" + BreakGlassSeed.Unique("svc"), "platform-admin")
            .GetAsync("/break-glass/audit", Ct);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task 件数の範囲外は400(int limit)
        => (await ClientAs(BreakGlassSeed.Unique("u")).GetAsync($"/break-glass/audit?limit={limit}", Ct)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);

    [Fact]
    public async Task 監査ログに資料の題名と本文は載らない()
    {
        var owner = BreakGlassSeed.Unique("owner");
        var doc = await BreakGlassSeed.PrivateNoteAsync(factory, owner, Ct);
        await ActivateAsync(ClientAs(owner), doc.Id, "理由", NewAdmin());

        var raw = await ClientAs(owner).GetStringAsync($"/break-glass/audit?documentId={doc.Id}", Ct);
        raw.Should().Contain(doc.Id.ToString(), "陽性対照: 対象資料の識別子は載る");
        raw.Should().NotContain(doc.Title).And.NotContain("\"title\"").And.NotContain("\"body\"");
    }

    // ── 保持 3 年 ──

    [Fact]
    public async Task 三年を超えた監査ログと発動記録は定期処理で消え_三年未満は残る()
    {
        var now = new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var owner = BreakGlassSeed.Unique("ret");
        Guid oldGrant, newGrant;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            var old = BreakGlassGrant.Activate(Guid.NewGuid(), owner, "admin", "理由", now.AddYears(-3).AddDays(-1));
            var recent = BreakGlassGrant.Activate(Guid.NewGuid(), owner, "admin", "理由", now.AddYears(-3).AddDays(1));
            db.BreakGlassGrants.AddRange(old, recent);
            db.BreakGlassAuditEntries.AddRange(BreakGlassAuditEntry.Activated(old), BreakGlassAuditEntry.Activated(recent));
            await db.SaveChangesAsync(Ct);
            (oldGrant, newGrant) = (old.Id, recent.Id);
        }

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PrivateNoteMaintenanceService>().RunAsync(now, Ct);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            (await db.BreakGlassGrants.Select(g => g.Id).ToListAsync(Ct))
                .Should().NotContain(oldGrant).And.Contain(newGrant, "3 年未満は残る（陽性対照）");
            (await db.BreakGlassAuditEntries.Select(a => a.GrantId).ToListAsync(Ct))
                .Should().NotContain(oldGrant).And.Contain(newGrant);
        }
    }
}
