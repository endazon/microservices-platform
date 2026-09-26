using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts.Dtos;

namespace DocumentService.Tests.Features.Documents;

// NFR-09, FR-19, FR-06, UC-03, 計画 ADR-0119 決定 3, ADR-0036 D-05・D-06・D-08, ADR-0034 決定 9, ADR-0056 (#1614):
// **REST の読み取り 5 口が、個人資料を所有者と共有先の利用者にだけ返す**ことを固定する。
//
// 🔴 **陰性（一覧に居ない・404）は必ず陽性対照（所有者には返る）と対にする。** 「常に 404 を返す実装」でも
// 陰性だけは緑になる。同じ資料・同じ口で、主体だけを替えて測る。
//
// 主体はヘッダで替える（`TestAuthHandler`）。機械の主体は `service-account-` の利用者名（`MachinePrincipal` の腕 A）。
// グループの共有先は認可サービスのスタブ（`StubDocumentReadScopeSource`）が許したときだけ通る。
// 本物の JwtBearer での 401 は `DocumentReadAuthenticationTests` が測る（ここは常に認証済み）。
[Trait("TestKind", "Integration")]
public class DocumentReadPrivateNoteTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];

    private HttpClient ClientAs(string user, params string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        if (roles.Length > 0)
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(",", roles));
        return client;
    }

    // 台帳へ直接入れる（作成の口は個人資料を拒むため）。共有は `(種別, 識別子)` の組で与える。
    private async Task<Document> SeedAsync(Dictionary<string, string> attributes,
        params (string Type, string Id)[] shares)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var doc = Document.Create($"doc-{Guid.NewGuid():N}", originalUri: null, contentType: null, attributes: attributes);
        db.Documents.Add(doc);
        foreach (var (type, id) in shares)
            db.DocumentShares.Add(DocumentShare.Create(doc.Id, type, id, grantedBy: "seed"));
        await db.SaveChangesAsync(Ct);
        return doc;
    }

    private static Dictionary<string, string> PrivateNote(string owner, string docScope = "private-note") => new()
    {
        ["confidentiality"] = "restricted",
        ["doc_scope"] = docScope,
        ["owner"] = owner,
    };

    private static Dictionary<string, string> Organization(string? owner = null)
    {
        var attributes = new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
            ["doc_scope"] = "organization",
        };
        if (owner is not null) attributes["owner"] = owner;
        return attributes;
    }

    // 1 つの主体から、1 つの文書へ読み取りの 4 口（一覧・個別・版の一覧・特定版）を引く。
    private async Task<ReadOutcome> ReadAllAsync(HttpClient client, Guid id)
    {
        var list = (await client.GetFromJsonAsync<List<DocumentDto>>("/documents", Ct))!;
        var get = await client.GetAsync($"/documents/{id}", Ct);
        var versions = await client.GetAsync($"/documents/{id}/versions", Ct);
        var version = await client.GetAsync($"/documents/{id}/versions/1", Ct);
        return new ReadOutcome(list.Any(d => d.Id == id), get.StatusCode, versions.StatusCode, version.StatusCode);
    }

    private sealed record ReadOutcome(bool InList, HttpStatusCode Get, HttpStatusCode Versions, HttpStatusCode Version)
    {
        public void ShouldBeReadable(string because)
        {
            InList.Should().BeTrue(because);
            Get.Should().Be(HttpStatusCode.OK, because);
            Versions.Should().Be(HttpStatusCode.OK, because);
            Version.Should().Be(HttpStatusCode.OK, because);
        }

        // 🔴 一覧に居ない ＋ 個別の 3 口がすべて 404（「無い」と区別しない。ADR-0056）。
        public void ShouldBeHidden(string because)
        {
            InList.Should().BeFalse(because);
            Get.Should().Be(HttpStatusCode.NotFound, because);
            Versions.Should().Be(HttpStatusCode.NotFound, because);
            Version.Should().Be(HttpStatusCode.NotFound, because);
        }
    }

    // ── AC-2 / AC-3: 他人には見えず、所有者には見える ───────────────────────────

    [Fact]
    public async Task 他人の個人資料は一覧に出ず個別の3口は404で_所有者には全部返る()
    {
        var alice = Unique("alice");
        var note = await SeedAsync(PrivateNote(alice));

        (await ReadAllAsync(ClientAs(alice), note.Id)).ShouldBeReadable("陽性対照: 所有者には返る");
        (await ReadAllAsync(ClientAs(Unique("mallory")), note.Id)).ShouldBeHidden("他人には存在ごと見えない");
    }

    // FR-19, ADR-0054: **`doc_scope` の値の大小が揺れていても個人資料として扱う**（`DocumentScopes.IsPrivateNote`）。
    [Theory]
    [InlineData("Private-Note")]
    [InlineData("PRIVATE-NOTE")]
    public async Task doc_scopeの大小が揺れた個人資料も他人には返らない(string docScope)
    {
        var alice = Unique("alice");
        var note = await SeedAsync(PrivateNote(alice, docScope));

        (await ReadAllAsync(ClientAs(alice), note.Id)).ShouldBeReadable("陽性対照: 所有者には返る");
        (await ReadAllAsync(ClientAs(Unique("mallory")), note.Id)).ShouldBeHidden("大小の揺れで組織文書扱いにならない");
    }

    // ADR-0036 D-08: **管理者ロールでも他人の個人資料は読めない**（平時は管理者も見ない）。
    [Fact]
    public async Task 管理者ロールでも他人の個人資料は読めない()
    {
        var note = await SeedAsync(PrivateNote(Unique("alice")));

        (await ReadAllAsync(ClientAs(Unique("admin"), "platform-admin"), note.Id))
            .ShouldBeHidden("管理者ロールは個人資料の読み取りを開かない");
    }

    // ── AC-3: 共有先 ───────────────────────────────────────────────────────

    // ADR-0036 D-06: 利用者の共有先は台帳だけで決まる（認可サービスへ問わない）。
    [Fact]
    public async Task 利用者の共有先は読め_共有されていない利用者は読めない()
    {
        var alice = Unique("alice");
        var bob = Unique("bob");
        var carol = Unique("carol");
        var note = await SeedAsync(PrivateNote(alice), (ShareSubjectType.User, bob));

        (await ReadAllAsync(ClientAs(bob), note.Id)).ShouldBeReadable("共有先の利用者には返る");
        (await ReadAllAsync(ClientAs(carol), note.Id)).ShouldBeHidden("共有されていない利用者には返らない");

        // 個別の口で測る（一覧はクラス内の他の試験のグループ共有の資料も対象に入り得るため）。
        var before = factory.ReadScopes.CallsFor(bob);
        (await ClientAs(bob).GetAsync($"/documents/{note.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        factory.ReadScopes.CallsFor(bob).Should().Be(before, "利用者の共有先は台帳だけで決まり、認可サービスへ問わない");
    }

    // ADR-0036 D-06, ADR-0098 決定 1: グループの共有先は**認可サービスが許したときだけ**通る
    // （所属は認可サービスが IdP から引く。ここではスタブが `shared_with ∈ {利用者, グループ}` の分岐を返す）。
    [Fact]
    public async Task グループの共有先は認可サービスが許したときだけ読める()
    {
        var alice = Unique("alice");
        var member = Unique("member");
        var outsider = Unique("outsider");
        var group = Guid.NewGuid().ToString("D");
        var note = await SeedAsync(PrivateNote(alice), (ShareSubjectType.Group, group));

        factory.ReadScopes.GrantSharedWith(member, member, group);
        factory.ReadScopes.GrantSharedWith(outsider, outsider, Guid.NewGuid().ToString("D"));

        (await ReadAllAsync(ClientAs(member), note.Id)).ShouldBeReadable("所属グループへ共有された資料は返る");
        (await ReadAllAsync(ClientAs(outsider), note.Id)).ShouldBeHidden("別のグループの利用者には返らない");
    }

    // 🔴 ADR-0036 D-08 を認可サービスの経路でも保つ: **静的属性だけの分岐（管理者の広いポリシー）は個人資料を開かない**
    // （`PrivateNoteVisibility.BranchMayGrant`。裁量の分岐〔owner / shared_with〕でなければ偽）。
    [Fact]
    public async Task 静的属性だけの分岐は個人資料を開かない()
    {
        var admin = Unique("admin");
        var note = await SeedAsync(PrivateNote(Unique("alice")), (ShareSubjectType.Group, Guid.NewGuid().ToString("D")));
        factory.ReadScopes.Grant(admin, [[new AttributeFilter("confidentiality", ["restricted", "internal"])]]);

        (await ReadAllAsync(ClientAs(admin, "platform-admin"), note.Id))
            .ShouldBeHidden("restricted を読めるポリシーでも個人資料は裁量の分岐でしか開かない");
    }

    // ── AC-6: fail-closed ─────────────────────────────────────────────────

    // 認可サービスを引けない（スタブの既定＝null）とき、グループの共有先の資料だけが見えなくなり、
    // 所有者の資料と組織文書の読み取りは止まらない。
    [Fact]
    public async Task 認可サービスを引けないときグループの共有先の資料だけが見えなくなる()
    {
        var alice = Unique("alice");
        var member = Unique("member");
        var group = Guid.NewGuid().ToString("D");
        var shared = await SeedAsync(PrivateNote(alice), (ShareSubjectType.Group, group));
        var own = await SeedAsync(PrivateNote(member));
        var org = await SeedAsync(Organization());

        // 宣言しない（＝引けない）。
        (await ReadAllAsync(ClientAs(member), shared.Id)).ShouldBeHidden("引けなければ読めない側へ倒す");
        (await ReadAllAsync(ClientAs(member), own.Id)).ShouldBeReadable("自分の資料は認可サービスに依らず読める");
        (await ReadAllAsync(ClientAs(member), org.Id)).ShouldBeReadable("組織文書は認可サービスに依らず読める");
    }

    // 🔴 問い合わせは**要求ごとに高々 1 回**（一覧に対象が何件あっても）。所有者・組織文書だけなら 0 回。
    [Fact]
    public async Task 一覧の中でグループ共有の資料が何件あっても認可サービスへは1回しか問わない()
    {
        var reader = Unique("reader");
        var group = Guid.NewGuid().ToString("D");
        for (var i = 0; i < 3; i++)
            await SeedAsync(PrivateNote(Unique("owner")), (ShareSubjectType.Group, group));
        factory.ReadScopes.GrantSharedWith(reader, reader, group);

        var list = (await ClientAs(reader).GetFromJsonAsync<List<DocumentDto>>("/documents", Ct))!;

        list.Count(d => DocumentScopes.IsPrivateNote(d.Attributes)).Should().BeGreaterThanOrEqualTo(3);
        factory.ReadScopes.CallsFor(reader).Should().Be(1, "判定の memo は要求の寿命で、利用者ごとに 1 回");
    }

    [Fact]
    public async Task 所有者と組織文書だけの読み取りでは認可サービスへ問わない()
    {
        var alice = Unique("alice");
        var own = await SeedAsync(PrivateNote(alice));
        var org = await SeedAsync(Organization());

        (await ClientAs(alice).GetAsync($"/documents/{own.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ClientAs(alice).GetAsync($"/documents/{org.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        factory.ReadScopes.CallsFor(alice).Should().Be(0);
    }

    // ── AC-4: 機械クライアント ───────────────────────────────────────────────

    // ADR-0034 決定 9: **機械の主体は組織文書を読めるが、個人資料は読めない**
    // （owner 属性に自分の名前があっても・共有先に居ても）。
    [Fact]
    public async Task 機械クライアントは組織文書を読めるが個人資料は読めない()
    {
        var machine = $"service-account-{Guid.NewGuid():N}"[..32];
        var org = await SeedAsync(Organization(owner: machine));
        var claimed = await SeedAsync(PrivateNote(machine), (ShareSubjectType.User, machine));
        factory.ReadScopes.GrantSharedWith(machine, machine);

        var client = ClientAs(machine, "platform-operator");
        (await ReadAllAsync(client, org.Id)).ShouldBeReadable("陽性対照: 組織文書は返る");
        (await ReadAllAsync(client, claimed.Id)).ShouldBeHidden("機械は所有者・共有先を名乗っても個人資料を読まない");
        factory.ReadScopes.CallsFor(machine).Should().Be(0, "機械の主体のために認可サービスへ問わない");
    }

    // `MachinePrincipal` の腕 B（利用者名が無く、クライアント識別がある）も機械である。
    [Fact]
    public async Task 利用者名を持たない機械クライアントも個人資料を読めない()
    {
        var org = await SeedAsync(Organization());
        var note = await SeedAsync(PrivateNote(Unique("alice")));

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.NoNameHeader, "1");
        client.DefaultRequestHeaders.Add(TestAuthHandler.ClientIdHeader, "some-machine");

        (await ReadAllAsync(client, org.Id)).ShouldBeReadable("陽性対照: 組織文書は返る");
        (await ReadAllAsync(client, note.Id)).ShouldBeHidden("機械には個人資料を返さない");
    }

    // ── /documents/page: 個人資料は主体に依らず返らない（組織文書の口） ──────────────

    [Fact]
    public async Task ページの口は所有者にも他人にも機械にも個人資料を返さない()
    {
        var alice = Unique("alice");
        var project = $"p-{Guid.NewGuid():N}";
        var attributes = PrivateNote(alice);
        attributes["project"] = project;
        var note = await SeedAsync(attributes);
        var orgAttributes = Organization();
        orgAttributes["project"] = project;
        var org = await SeedAsync(orgAttributes);

        foreach (var client in new[]
                 {
                     ClientAs(alice), ClientAs(Unique("mallory")),
                     ClientAs($"service-account-{Guid.NewGuid():N}"[..32], "platform-operator"),
                 })
        {
            var page = (await client.GetFromJsonAsync<DocumentPageDto>($"/documents/page?attr.project={project}", Ct))!;
            page.Items.Select(d => d.Id).Should().Equal([org.Id], "組織文書は返り（陽性対照）、個人資料は返らない");
            page.Items.Should().NotContain(d => d.Id == note.Id);
        }
    }
}
