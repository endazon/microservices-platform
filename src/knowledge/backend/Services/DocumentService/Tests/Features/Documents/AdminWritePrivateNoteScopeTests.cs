using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.Documents.PutBody;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.Documents;

// FR-06, FR-19, UC-03, SC-05, NFR-09, 計画 ADR-0036 D-08, ADR-0119 決定 3, ADR-0056 決定 1, [[IADR-0044]] (#1629):
// **管理の書き込み口（PUT・PATCH metadata・publish・archive・DELETE）とタグの反映口の管理者の分岐は、
// 他人の個人資料に作用せず、応答にも中身を出さない**ことを固定する。
//
// 🔴 **陰性（404・不変・イベント無し）は必ず陽性対照と対にする。** 「常に 404 を返す実装」でも陰性だけは緑になる。
// 同じ口・同じ主体で、組織文書には作用できることを同じテストの中で示す。
//
// 主体は人の管理者（`platform-admin`）と機械の管理者（`service-account-abac-seeder` ＋ `platform-admin`。
// realm の ABAC 投入用クライアントと同じ形）。🔴 **所有者が管理者でも 5 口では 404 である**（除外は所有者を問わず一律。
// `DocumentManageScope` の注記。所有者の経路は FR-19 の口であり、その陽性対照は下の `所有者の経路は…` が持つ）。
//
// 🔴 **変異試験の対象である**: 5 口のどれか 1 つで `DocumentManageScope.FindManageableAsync` を
// `db.Documents.FindAsync` へ戻すと、その口の行の `…404で_中身も出さず_何も変えない` が落ちる。
[Trait("TestKind", "Integration")]
public class AdminWritePrivateNoteScopeTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private const string AdminRole = "platform-admin";
    private const string MachineAdmin = "service-account-abac-seeder";

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];

    private RecordingMessageBus Bus => factory.Services.GetRequiredService<RecordingMessageBus>();

    private HttpClient ClientAs(string user, params string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        if (roles.Length > 0)
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(",", roles));
        if (user == MachineAdmin)
            client.DefaultRequestHeaders.Add(TestAuthHandler.ClientIdHeader, "abac-seeder");
        return client;
    }

    private HttpClient PrincipalClient(string principal, string owner) => principal switch
    {
        "human-admin" => ClientAs(Unique("admin"), AdminRole),
        "machine-admin" => ClientAs(MachineAdmin, AdminRole),
        "owner-admin" => ClientAs(owner, AdminRole),
        _ => throw new ArgumentOutOfRangeException(nameof(principal)),
    };

    // 台帳へ直接入れる（作成の口は個人資料を拒むため）。
    private async Task<Document> SeedAsync(Dictionary<string, string> attributes)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var doc = Document.Create($"secret-title-{Guid.NewGuid():N}", originalUri: null, contentType: null,
            attributes: attributes);
        db.Documents.Add(doc);
        await db.SaveChangesAsync(Ct);
        return doc;
    }

    private async Task<Document?> LoadAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        return await db.Documents.FindAsync([id], Ct);
    }

    private static Dictionary<string, string> PrivateNote(string owner) => new()
    {
        ["confidentiality"] = "restricted",
        ["doc_scope"] = "private-note",
        ["owner"] = owner,
    };

    private static Dictionary<string, string> Organization(string owner) => new()
    {
        ["confidentiality"] = "internal",
        ["doc_scope"] = "organization",
        ["owner"] = owner,
    };

    // 5 口を 1 か所で列挙する。`PUT` / `PATCH` の本文は `doc_scope` を変えない（不変性の 400 に化けさせない）
    // うえで `owner` を呼び出し元へ書き換えようとする —— 門が無ければ所有権の奪取まで届く形である。
    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string operation, Guid id,
        Dictionary<string, string> currentAttributes, string takeoverOwner, CancellationToken ct)
    {
        var takeover = new Dictionary<string, string>(currentAttributes) { ["owner"] = takeoverOwner };
        return operation switch
        {
            "PUT" => client.PutAsJsonAsync($"/documents/{id}",
                new { title = "奪った題名", attributes = takeover, tags = new List<string>() }, ct),
            "PATCH" => client.PatchAsJsonAsync($"/documents/{id}/metadata",
                new { attributes = takeover, tags = new List<string>() }, ct),
            "PUBLISH" => client.PostAsync($"/documents/{id}/publish", null, ct),
            "ARCHIVE" => client.PostAsync($"/documents/{id}/archive", null, ct),
            "DELETE" => client.DeleteAsync($"/documents/{id}", ct),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    private static HttpStatusCode Success(string operation) =>
        operation == "DELETE" ? HttpStatusCode.NoContent : HttpStatusCode.OK;

    public static TheoryData<string, string> Matrix()
    {
        var data = new TheoryData<string, string>();
        foreach (var op in new[] { "PUT", "PATCH", "PUBLISH", "ARCHIVE", "DELETE" })
            foreach (var principal in new[] { "human-admin", "machine-admin", "owner-admin" })
                data.Add(op, principal);
        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task 管理の書き込み口は個人資料に404で_中身も出さず_何も変えない(string operation, string principal)
    {
        var alice = Unique("alice");
        var note = await SeedAsync(PrivateNote(alice));
        var client = PrincipalClient(principal, owner: alice);
        var updatesBefore = Bus.PublishedOf<DocumentUpdated>().Count(e => e.DocumentId == note.Id);

        var res = await SendAsync(client, operation, note.Id, PrivateNote(alice), takeoverOwner: "taker", Ct);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "個人資料は管理の口の対象外（存在を明かさない）");
        var body = await res.Content.ReadAsStringAsync(Ct);
        body.Should().NotContain(note.Title, "表題を返さない")
            .And.NotContain(alice, "owner を返さない")
            .And.NotContain(note.Id.ToString(), "文書を名指す応答を返さない");

        var stored = await LoadAsync(note.Id);
        stored.Should().NotBeNull("削除されていない");
        stored!.Title.Should().Be(note.Title);
        stored.Status.Should().Be(note.Status, "公開・保管されていない");
        stored.Version.Should().Be(note.Version, "版が進んでいない");
        stored.Attributes.Should().Contain("owner", alice, "owner を奪えない");

        Bus.PublishedOf<DocumentUpdated>().Count(e => e.DocumentId == note.Id)
            .Should().Be(updatesBefore, "下流へ何も流さない");
        Bus.PublishedOf<DocumentDeleted>().Should().NotContain(e => e.DocumentId == note.Id);

        // ★ 陽性対照: 同じ主体・同じ口で、組織文書には作用できる（門が「常に 404」ではない）。
        var org = await SeedAsync(Organization(alice));
        var control = await SendAsync(PrincipalClient(principal, owner: alice), operation, org.Id,
            Organization(alice), takeoverOwner: alice, Ct);
        control.StatusCode.Should().Be(Success(operation), "陽性対照: 組織文書は管理の口の対象である");
    }

    // 不在の ID と他人の個人資料は、応答の形で区別できない（ADR-0056 決定 1。状態コードと本文が同じ）。
    [Theory]
    [InlineData("PUBLISH")]
    [InlineData("DELETE")]
    public async Task 他人の個人資料と不在のIDは応答で区別できない(string operation)
    {
        var note = await SeedAsync(PrivateNote(Unique("alice")));
        var admin = ClientAs(Unique("admin"), AdminRole);

        var hidden = await SendAsync(admin, operation, note.Id, PrivateNote("x"), "x", Ct);
        var missing = await SendAsync(admin, operation, Guid.NewGuid(), PrivateNote("x"), "x", Ct);

        hidden.StatusCode.Should().Be(missing.StatusCode);
        (await hidden.Content.ReadAsStringAsync(Ct)).Should().Be(await missing.Content.ReadAsStringAsync(Ct));
    }

    // ── タグの反映口（`POST /documents/{id}/tags`）の管理者の分岐 ─────────────────────

    private async Task<string> RegisterTagAsync()
    {
        var name = $"tag-{Guid.NewGuid():N}";
        var resp = await ClientAs(Unique("admin"), AdminRole)
            .PostAsJsonAsync("/tags", new CreateTagRequest(name), Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return name;
    }

    [Theory]
    [InlineData("human-admin")]
    [InlineData("machine-admin")]
    public async Task タグの反映口の管理者の分岐は他人の個人資料に及ばず_所有者と組織文書には届く(string principal)
    {
        var tag = await RegisterTagAsync();
        var alice = Unique("alice");
        var note = await SeedAsync(PrivateNote(alice));

        var denied = await PrincipalClient(principal, owner: alice)
            .PostAsJsonAsync($"/documents/{note.Id}/tags", new AddDocumentTagRequest(tag), Ct);

        denied.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await denied.Content.ReadAsStringAsync(Ct)).Should().NotContain(note.Title).And.NotContain(alice);
        (await LoadAsync(note.Id))!.Version.Should().Be(note.Version, "タグは付いていない");

        // ★ 陽性対照 1: 同じ資料へ、所有者（ロールなし）は足せる（FR-19 の所有者の束縛は生きている）。
        var byOwner = await ClientAs(alice, "viewer")
            .PostAsJsonAsync($"/documents/{note.Id}/tags", new AddDocumentTagRequest(tag), Ct);
        byOwner.StatusCode.Should().Be(HttpStatusCode.OK);
        (await byOwner.Content.ReadFromJsonAsync<DocumentDto>(Ct))!.Tags.Should().Contain(tag);

        // ★ 陽性対照 2: 同じ主体は、他人の組織文書へは足せる（管理者の分岐は組織文書に残る）。
        var org = await SeedAsync(Organization(alice));
        var byAdmin = await PrincipalClient(principal, owner: alice)
            .PostAsJsonAsync($"/documents/{org.Id}/tags", new AddDocumentTagRequest(tag), Ct);
        byAdmin.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── 所有者の経路は変わらない ─────────────────────────────────────────────

    // FR-21, FR-19: 本文の投入（所有者の動的束縛。ロールなし）は、同じ個人資料で所有者に通り、管理者に通らない。
    [Fact]
    public async Task 所有者の経路は個人資料に通り続け_管理者は通らない()
    {
        var alice = Unique("alice");
        var note = await SeedAsync(PrivateNote(alice));

        var byOwner = await ClientAs(alice, "viewer")
            .PutAsJsonAsync($"/documents/{note.Id}/body", new UpdateDocumentBodyRequest("# 本人の本文"), Ct);
        var byAdmin = await ClientAs(Unique("admin"), AdminRole)
            .PutAsJsonAsync($"/documents/{note.Id}/body", new UpdateDocumentBodyRequest("# 管理者"), Ct);

        byOwner.StatusCode.Should().Be(HttpStatusCode.OK, "陽性対照: 所有者の経路は変わらない");
        byAdmin.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
