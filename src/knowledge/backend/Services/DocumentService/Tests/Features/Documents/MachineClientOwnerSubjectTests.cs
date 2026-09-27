using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.Documents;

// FR-06, FR-21, FR-20, FR-18, FR-08, UC-03, 計画 ADR-0122 実測 8・フォローアップ 3, ADR-0119 決定 2, ADR-0036 D-07, ADR-0056,
// [[IADR-0044]] (#1679):
// **所有者で許す口（本文の投入・共有の付与／一覧／取り消し・タグ反映）の主体は、作成の口が `owner` へ入れる名前と同じ関数から引く。**
//
// 🔴 **腕 B（利用者名が無くクライアント識別だけの機械）が本件の主対象である。** 作成の口は腕 B にも
// `service-account-<clientId>` を `owner` として入れるが、従前の本文の投入は `Identity.Name`（null）で比べていたので、
// **自分で作った文書に本文を入れられなかった**（ADR-0122 実測 8。本クラスの本文の試験は変更前のコードで 404 の赤）。
//
// 🔴 **陽性と対照を対で置く。** 「誰でも書ける」でも陽性だけは緑になるので、別の機械（腕 A・腕 B）と人の文書に
// 同じ主体で書けないことを同じクラスの中で示す。人の利用者の既存の挙動も固定する。
[Trait("TestKind", "Integration")]
public class MachineClientOwnerSubjectTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private const string KbWriterClientId = "ai-stock-trading-kb-writer";
    private const string KbWriter = "service-account-" + KbWriterClientId;
    private const string OtherClientId = "other-writer";
    private const string OtherMachine = "service-account-" + OtherClientId;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient ClientAs(string user, params string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(",", roles));
        return client;
    }

    // 腕 A: `preferred_username = service-account-<clientId>`。
    private HttpClient NamedMachine(string user = KbWriter) => ClientAs(user, "platform-operator");

    // 腕 B: 利用者名を持たず、クライアント識別だけを持つ（`profile` スコープの無い client credentials。AST の KB 用クライアントの見込みの形）。
    private HttpClient NamelessMachine(string clientId = KbWriterClientId)
    {
        var client = ClientAs("ignored", "platform-operator");
        client.DefaultRequestHeaders.Add(TestAuthHandler.NoNameHeader, "1");
        client.DefaultRequestHeaders.Add(TestAuthHandler.ClientIdHeader, clientId);
        return client;
    }

    // 名前もクライアント識別も無い主体（機械とも人とも決まらない）。
    private HttpClient NoSubject()
    {
        var client = ClientAs("ignored", "platform-operator");
        client.DefaultRequestHeaders.Add(TestAuthHandler.NoNameHeader, "1");
        return client;
    }

    private async Task<DocumentDto> CreateAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/documents", new
        {
            title = $"kb-{Guid.NewGuid():N}",
            attributes = new Dictionary<string, string>
            {
                ["confidentiality"] = "internal",
                ["doc_scope"] = "organization",
            },
            tags = new List<string>(),
        }, Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<DocumentDto>(Ct))!;
    }

    private Task<HttpResponseMessage> PutBodyAsync(HttpClient client, Guid id, string body)
        => client.PutAsJsonAsync($"/documents/{id}/body", new { body }, Ct);

    private async Task<Document?> LoadAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        return await db.Documents.FindAsync([id], Ct);
    }

    private async Task<string> RegisterTagAsync()
    {
        var name = $"tag-{Guid.NewGuid():N}";
        var resp = await ClientAs("tag-admin", "platform-admin")
            .PostAsJsonAsync("/tags", new CreateTagRequest(name), Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return name;
    }

    // ── T-73: 本文の投入 ─────────────────────────────────────────────

    [Fact]
    public async Task 利用者名の無いサービスアカウントは自分の作った文書に本文を入れられる()
    {
        var created = await CreateAsync(NamelessMachine());
        created.Attributes.Should().Contain("owner", KbWriter, "前提: 作成の口は腕 B もサービスアカウント名を owner にする");

        var put = await PutBodyAsync(NamelessMachine(), created.Id, "AST の入れ直しの本文");

        put.StatusCode.Should().Be(HttpStatusCode.OK,
            "ADR-0122 フォローアップ 3: 作成の口と同じ主体の規則で比べる（Identity.Name だけで比べると 404）");
        var stored = await LoadAsync(created.Id);
        stored!.MarkdownUri.Should().NotBeNull("本文が格納された");
        stored.ContentFingerprint.Should().Be(DocumentBodyIntake.Fingerprint("AST の入れ直しの本文"));

        (await PutBodyAsync(NamedMachine(), created.Id, "腕 A から"))
            .StatusCode.Should().Be(HttpStatusCode.OK, "同じクライアントは profile スコープの有無で別の所有者にならない");
    }

    [Fact]
    public async Task 利用者名の無いサービスアカウントも自分が腕Aで作った文書に本文を入れられる()
    {
        var created = await CreateAsync(NamedMachine());

        (await PutBodyAsync(NamelessMachine(), created.Id, "腕 B から"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 別のサービスアカウントの文書と人の文書には本文を入れられず_何も変わらない()
    {
        var byOtherNameless = await CreateAsync(NamelessMachine(OtherClientId));
        var byOtherNamed = await CreateAsync(NamedMachine(OtherMachine));
        var byHuman = await CreateAsync(ClientAs("alice", "platform-admin"));
        byOtherNameless.Attributes.Should().Contain("owner", OtherMachine);
        byHuman.Attributes.Should().Contain("owner", "alice");

        foreach (var doc in new[] { byOtherNameless, byOtherNamed, byHuman })
        {
            var put = await PutBodyAsync(NamelessMachine(), doc.Id, "他人の文書へ");
            put.StatusCode.Should().Be(HttpStatusCode.NotFound, "所有者でなければ 404（ADR-0056。実在を明かさない）");
            (await LoadAsync(doc.Id))!.MarkdownUri.Should().BeNull("本文は入っていない");
        }

        // ★ 陽性対照: 同じ文書に所有者自身は入れられる（主体が判定の入力であること）。
        (await PutBodyAsync(NamelessMachine(OtherClientId), byOtherNameless.Id, "所有者から"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 人の利用者の本文の投入は従前どおり所有者だけが通る()
    {
        var doc = await CreateAsync(ClientAs("alice", "platform-admin"));

        (await PutBodyAsync(ClientAs("alice"), doc.Id, "alice の本文")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PutBodyAsync(ClientAs("bob", "platform-admin"), doc.Id, "bob の本文"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "管理者ロールでも所有者でなければ書けない（ロールで判定しない）");
        (await PutBodyAsync(NoSubject(), doc.Id, "主体なし"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "名前もクライアント識別も無い主体は誰の文書にも書けない");
        (await PutBodyAsync(NamelessMachine(), doc.Id, "機械"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── T-74: 共有の付与・一覧・取り消しとタグ反映（同じ所有者の比較を持つ口） ─────────────

    [Fact]
    public async Task 利用者名の無いサービスアカウントは自分の文書の共有を付与し一覧し取り消せる_付与者はサービスアカウント名()
    {
        var doc = await CreateAsync(NamelessMachine());

        var grant = await NamelessMachine().PostAsJsonAsync($"/documents/{doc.Id}/shares",
            new { subjectType = "user", subjectId = "bob" }, Ct);
        grant.StatusCode.Should().Be(HttpStatusCode.Created);

        var list = await NamelessMachine().GetAsync($"/documents/{doc.Id}/shares", Ct);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var shares = (await list.Content.ReadFromJsonAsync<List<DocumentShareDto>>(Ct))!;
        shares.Should().ContainSingle().Which.GrantedBy.Should().Be(KbWriter, "付与者は owner と同じ主体名で記録する");

        (await NamelessMachine().DeleteAsync($"/documents/{doc.Id}/shares/user/bob", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task 別のサービスアカウントの文書の共有は付与も一覧も取り消しもできない()
    {
        var doc = await CreateAsync(NamelessMachine(OtherClientId));
        (await NamelessMachine(OtherClientId).PostAsJsonAsync($"/documents/{doc.Id}/shares",
            new { subjectType = "user", subjectId = "bob" }, Ct)).StatusCode.Should().Be(HttpStatusCode.Created, "陽性対照");

        (await NamelessMachine().PostAsJsonAsync($"/documents/{doc.Id}/shares",
            new { subjectType = "user", subjectId = "carol" }, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await NamelessMachine().GetAsync($"/documents/{doc.Id}/shares", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await NamelessMachine().DeleteAsync($"/documents/{doc.Id}/shares/user/bob", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task 利用者名の無いサービスアカウントは自分の文書にタグを足せ_別のサービスアカウントの文書には足せない()
    {
        var tag = await RegisterTagAsync();
        var own = await CreateAsync(NamelessMachine());
        var others = await CreateAsync(NamelessMachine(OtherClientId));

        (await NamelessMachine().PostAsJsonAsync($"/documents/{own.Id}/tags", new AddDocumentTagRequest(tag), Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await NamelessMachine().PostAsJsonAsync($"/documents/{others.Id}/tags", new AddDocumentTagRequest(tag), Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "運用者ロールの機械は管理者の分岐に当たらず、所有者でもない");
        (await LoadAsync(others.Id))!.Tags.Should().BeEmpty();
    }
}
