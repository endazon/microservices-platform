using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.Documents;

// FR-06, FR-08, FR-19, UC-03, SC-05, 計画 ADR-0119 決定 2, ADR-0036 D-01・D-07・§未確定事項 3, ADR-0056, ADR-0058,
// [[IADR-0044]] [[IADR-0075]] (#1616):
// **機械クライアントは、自分が `owner` の組織文書に限りメタデータ更新と削除を行える。`owner` と `doc_scope` は動かせない。**
//
// 🔴 **陰性は陽性対照と対で置く。** 「機械は常に 403」でも陰性だけは緑になる。同じ主体・同じ口で、自分の文書には
// 作用できることを同じクラスの中で示す。人の管理者の経路（従前どおり全組織文書に作用できる）も対照に置く。
//
// 主体（`TestAuthHandler`）:
//   - 機械（腕 A）: 利用者名 `service-account-ai-stock-trading-kb-writer` ＋ `platform-operator`（realm の AST の KB 用クライアントと同じ形）
//   - 機械（腕 B）: 利用者名なし ＋ クライアント識別 `ai-stock-trading-kb-writer` ＋ `platform-operator`
//   - 運用者だけの人: 利用者名 ＋ `platform-operator`
//   - 人の管理者: 利用者名 ＋ `platform-admin`
//
// 🔴 **変異試験の対象である**: `DocumentManageScope.DenyUnlessAdminOrMachineOwnerAsync` の所有者の比較を外すと
// `他の主体が所有する文書は…` が、`OwnerChangedProblemOrNull` の呼び出しを外すと `owner の書き換えは…` が落ちる。
[Trait("TestKind", "Integration")]
public class MachineClientOwnDocumentWriteTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private const string KbWriterClientId = "ai-stock-trading-kb-writer";
    private const string KbWriter = "service-account-" + KbWriterClientId;
    private const string OtherMachine = "service-account-other-writer";

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];

    private RecordingMessageBus Bus => factory.Services.GetRequiredService<RecordingMessageBus>();

    private HttpClient ClientAs(string user, params string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(",", roles));
        return client;
    }

    // 腕 A: `preferred_username = service-account-<clientId>`。
    private HttpClient Machine(string user = KbWriter) => ClientAs(user, "platform-operator");

    // 腕 B: 利用者名を持たず、クライアント識別だけを持つ（`profile` スコープの無い client credentials）。
    private HttpClient NamelessMachine(string clientId = KbWriterClientId)
    {
        var client = ClientAs("ignored", "platform-operator");
        client.DefaultRequestHeaders.Add(TestAuthHandler.NoNameHeader, "1");
        client.DefaultRequestHeaders.Add(TestAuthHandler.ClientIdHeader, clientId);
        return client;
    }

    private HttpClient HumanAdmin() => ClientAs(Unique("admin"), "platform-admin");

    private static Dictionary<string, string> Org(string? owner = null)
    {
        var attributes = new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
            ["doc_scope"] = "organization",
        };
        if (owner is not null) attributes["owner"] = owner;
        return attributes;
    }

    private async Task<DocumentDto> CreateAsync(HttpClient client, Dictionary<string, string>? attributes = null)
    {
        var resp = await client.PostAsJsonAsync("/documents", new
        {
            title = $"kb-{Guid.NewGuid():N}",
            attributes = attributes ?? Org(),
            tags = new List<string>(),
        }, Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<DocumentDto>(Ct))!;
    }

    // 台帳へ直接入れる（所有者を任意に置くため。作成の口は所有者を主体から決める）。
    private async Task<Document> SeedAsync(Dictionary<string, string> attributes)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var doc = Document.Create($"seed-{Guid.NewGuid():N}", originalUri: null, contentType: null, attributes: attributes);
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

    private Task<HttpResponseMessage> PatchAsync(HttpClient client, Guid id, Dictionary<string, string> attributes)
        => client.PatchAsJsonAsync($"/documents/{id}/metadata",
            new { attributes, tags = new List<string>(), changeNote = "kb-refresh" }, Ct);

    // ── T-64: 作成した機械クライアントが owner になり、その文書を更新・削除できる ─────────────

    [Fact]
    public async Task 機械クライアントが作る文書のownerはそのサービスアカウントで_要求のownerは捨てられる()
    {
        var created = await CreateAsync(Machine(), Org(owner: "someone-else"));

        created.Attributes.Should().Contain("owner", KbWriter, "ADR-0119 決定 2: 機械の経路の owner はサービスアカウント");
    }

    [Fact]
    public async Task 機械クライアントは自分がownerの組織文書のメタデータを更新でき_削除もできる()
    {
        var created = await CreateAsync(Machine());

        var patched = await PatchAsync(Machine(), created.Id,
            new Dictionary<string, string>(created.Attributes) { ["confidentiality"] = "confidential" });
        patched.StatusCode.Should().Be(HttpStatusCode.OK, "自分が owner の組織文書のメタデータ更新は許される");
        var dto = (await patched.Content.ReadFromJsonAsync<DocumentDto>(Ct))!;
        dto.Attributes.Should().Contain("confidentiality", "confidential")
            .And.Contain("owner", KbWriter).And.Contain("doc_scope", "organization");
        dto.Version.Should().Be(created.Version + 1);

        var deleted = await Machine().DeleteAsync($"/documents/{created.Id}", Ct);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent, "自分が owner の組織文書は削除できる");
        (await LoadAsync(created.Id)).Should().BeNull();
        Bus.PublishedOf<DocumentDeleted>().Should().Contain(e => e.DocumentId == created.Id,
            "削除の伝播（ADR-0057）は経路によらず同じ");
    }

    // ── T-65: 腕 B（利用者名なし）でも同じサービスアカウントとして扱う ─────────────────────

    [Fact]
    public async Task 利用者名を持たない機械クライアントも同じサービスアカウントとしてownerになり_書ける()
    {
        var created = await CreateAsync(NamelessMachine());
        created.Attributes.Should().Contain("owner", KbWriter,
            "腕 B も Keycloak の規約（service-account-<clientId>）で同じ所有者になる");

        (await PatchAsync(NamelessMachine(), created.Id, new Dictionary<string, string>(created.Attributes)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await PatchAsync(Machine(), created.Id, new Dictionary<string, string>(created.Attributes)))
            .StatusCode.Should().Be(HttpStatusCode.OK, "腕 A の同じクライアントも同じ所有者として書ける");
    }

    // ── T-66: 他の主体の文書・個人資料は対象外 ─────────────────────────────────────

    [Theory]
    [InlineData("alice")]
    [InlineData(OtherMachine)]
    [InlineData(null)]
    public async Task 他の主体が所有する文書は機械クライアントが更新も削除もできず_何も変わらない(string? owner)
    {
        var doc = await SeedAsync(Org(owner));

        var patch = await PatchAsync(Machine(), doc.Id, new Dictionary<string, string>(Org(owner)) { ["confidentiality"] = "public" });
        var delete = await Machine().DeleteAsync($"/documents/{doc.Id}", Ct);

        // ADR-0056: 読めるが書けない文書は 403、読めない文書は 404（「読めるか」は読み取りの判定点が答える。
        // 組織文書の内容の ABAC〔#1615〕が入ると、機械が読めない文書は 404 になる —— どちらでも「作用しない」が主張である）。
        patch.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
        delete.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
        (await patch.Content.ReadAsStringAsync(Ct)).Should().NotContain(doc.Title, "中身を返さない");

        var stored = await LoadAsync(doc.Id);
        stored.Should().NotBeNull("削除されていない");
        stored!.Version.Should().Be(doc.Version);
        stored.Attributes.Should().Contain("confidentiality", "internal");

        // ★ 陽性対照: 人の管理者は同じ文書を更新できる（管理者の経路は従前どおり）。
        (await PatchAsync(HumanAdmin(), doc.Id, Org(owner))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // FR-06, ADR-0119 決定 2, ADR-0036 D-07 (#1616・監査 F2): **所有者の比較は序数一致（大文字小文字を区別する）。**
    // 大小だけ違う `owner` を「自分の文書」と読むと、別の主体名の文書へ書けてしまう（本文の投入・個人資料の読み取りと同じ比較を
    // 機械の経路でも通っていることの端点越しの固定。大小を無視する変異は、これまで単体試験でしか落ちなかった）。
    [Theory]
    [InlineData("Service-Account-Ai-Stock-Trading-Kb-Writer")]
    [InlineData("SERVICE-ACCOUNT-AI-STOCK-TRADING-KB-WRITER")]
    public async Task 大小だけ違うownerは機械クライアントの自分の文書として扱わない(string caseVariant)
    {
        var doc = await SeedAsync(Org(owner: caseVariant));

        var patch = await PatchAsync(Machine(), doc.Id,
            new Dictionary<string, string>(Org(caseVariant)) { ["confidentiality"] = "public" });
        var delete = await Machine().DeleteAsync($"/documents/{doc.Id}", Ct);

        patch.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
        delete.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
        var stored = await LoadAsync(doc.Id);
        stored.Should().NotBeNull("削除されていない");
        stored!.Version.Should().Be(doc.Version);
        stored.Attributes.Should().Contain("confidentiality", "internal");

        // ★ 陽性対照: 大小まで一致する owner の文書なら、同じ主体・同じ口で書ける。
        var exact = await SeedAsync(Org(owner: KbWriter));
        (await PatchAsync(Machine(), exact.Id, Org(KbWriter))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 機械クライアントがownerを名乗る個人資料もこの経路の対象外で404()
    {
        var note = await SeedAsync(new Dictionary<string, string>
        {
            ["confidentiality"] = "restricted",
            ["doc_scope"] = "private-note",
            ["owner"] = KbWriter,
        });

        var patch = await PatchAsync(Machine(), note.Id, new Dictionary<string, string>(note.Attributes));
        var delete = await Machine().DeleteAsync($"/documents/{note.Id}", Ct);

        patch.StatusCode.Should().Be(HttpStatusCode.NotFound, "ADR-0119 決定 2: 個人資料には及ばない");
        delete.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LoadAsync(note.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task 不在のIDは機械クライアントにも404()
    {
        (await Machine().DeleteAsync($"/documents/{Guid.NewGuid()}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── T-67: owner・doc_scope は機械の経路で動かせない ────────────────────────────

    [Fact]
    public async Task owner_の書き換えは機械クライアントの経路で400になり_送らなければ現在の値が残る()
    {
        var created = await CreateAsync(Machine());

        var toOther = await PatchAsync(Machine(), created.Id,
            new Dictionary<string, string>(created.Attributes) { ["owner"] = "alice" });
        toOther.StatusCode.Should().Be(HttpStatusCode.BadRequest, "owner を他者へ渡せない");
        (await toOther.Content.ReadAsStringAsync(Ct)).Should().Contain("owner");

        var toEmpty = await PatchAsync(Machine(), created.Id,
            new Dictionary<string, string>(created.Attributes) { ["owner"] = "" });
        toEmpty.StatusCode.Should().Be(HttpStatusCode.BadRequest, "owner を空にもできない");

        var withoutOwner = new Dictionary<string, string>(created.Attributes);
        withoutOwner.Remove("owner");
        var kept = await PatchAsync(Machine(), created.Id, withoutOwner);
        kept.StatusCode.Should().Be(HttpStatusCode.OK, "送らないことは書き換えではない");
        (await kept.Content.ReadFromJsonAsync<DocumentDto>(Ct))!.Attributes
            .Should().Contain("owner", KbWriter, "属性の全置換でも owner は落ちない");

        (await LoadAsync(created.Id))!.Attributes.Should().Contain("owner", KbWriter);
    }

    [Fact]
    public async Task doc_scope_の書き換えは機械クライアントの経路で400になる()
    {
        var created = await CreateAsync(Machine());

        var res = await PatchAsync(Machine(), created.Id,
            new Dictionary<string, string>(created.Attributes) { ["doc_scope"] = "private-note" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("doc_scope");
        (await LoadAsync(created.Id))!.Attributes.Should().Contain("doc_scope", "organization");
    }

    // ── T-68: 人の利用者には管理者限定がそのまま効く ──────────────────────────────

    [Theory]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task 運用者だけの人は自分がownerの文書でも従来どおり403(string method)
    {
        var bob = Unique("bob");
        var doc = await SeedAsync(Org(owner: bob));
        var bobClient = ClientAs(bob, "platform-operator");

        var own = method == "PATCH"
            ? await PatchAsync(bobClient, doc.Id, Org(bob))
            : await bobClient.DeleteAsync($"/documents/{doc.Id}", Ct);
        var missing = method == "PATCH"
            ? await PatchAsync(bobClient, Guid.NewGuid(), Org(bob))
            : await bobClient.DeleteAsync($"/documents/{Guid.NewGuid()}", Ct);

        own.StatusCode.Should().Be(HttpStatusCode.Forbidden, "SC-05: 人の利用者の破壊的操作は管理者限定");
        missing.StatusCode.Should().Be(HttpStatusCode.Forbidden, "文書の有無に依らない（入口の門）");
        (await LoadAsync(doc.Id))!.Version.Should().Be(doc.Version);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PUBLISH")]
    [InlineData("ARCHIVE")]
    public async Task 機械クライアントに開いたのはメタデータ更新と削除だけで_編集_公開_保管は403のまま(string op)
    {
        var created = await CreateAsync(Machine());
        var client = Machine();

        var res = op switch
        {
            "PUT" => await client.PutAsJsonAsync($"/documents/{created.Id}",
                new { title = "renamed", attributes = created.Attributes, tags = new List<string>() }, Ct),
            "PUBLISH" => await client.PostAsync($"/documents/{created.Id}/publish", null, Ct),
            _ => await client.PostAsync($"/documents/{created.Id}/archive", null, Ct),
        };

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "ADR-0119 決定 2 の射程はメタデータ更新と削除だけ");
    }

    [Fact]
    public async Task 書き込みのロールを持たない機械クライアントは自分がownerでも403()
    {
        var doc = await SeedAsync(Org(owner: KbWriter));

        var res = await PatchAsync(ClientAs(KbWriter, "viewer"), doc.Id, Org(KbWriter));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "書き込みの群の下限（admin / operator）はそのまま");
    }

    // ── T-69: 人の管理者の経路でも owner は動かせない（判断。IADR-0044 の 2026-09-27 追記） ─────

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task 人の管理者もownerを書き換えられず_送らなければ現在の値が残る(string method)
    {
        var doc = await SeedAsync(Org(owner: "alice"));
        var admin = HumanAdmin();

        Task<HttpResponseMessage> Save(Dictionary<string, string> attributes) => method == "PUT"
            ? admin.PutAsJsonAsync($"/documents/{doc.Id}",
                new { title = doc.Title, attributes, tags = new List<string>() }, Ct)
            : PatchAsync(admin, doc.Id, attributes);

        var transfer = await Save(Org(owner: KbWriter));
        transfer.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "所有者の移管は計画で未確定（ADR-0036 §未確定事項 3）。管理者が機械へ書き込み権限を渡す形を作らない");
        (await LoadAsync(doc.Id))!.Attributes.Should().Contain("owner", "alice");

        var kept = await Save(Org());
        kept.StatusCode.Should().Be(HttpStatusCode.OK, "★ 陽性対照: 管理者の編集そのものは従前どおり通る");
        (await LoadAsync(doc.Id))!.Attributes.Should().Contain("owner", "alice");

        var same = await Save(Org(owner: "alice"));
        same.StatusCode.Should().Be(HttpStatusCode.OK, "SC-05 の編集画面は DTO の属性（owner を含む）を送り返す");
    }

    [Fact]
    public async Task ownerを持たない文書へ管理者がownerを付けることもできない()
    {
        var doc = await SeedAsync(Org());

        var res = await PatchAsync(HumanAdmin(), doc.Id, Org(owner: KbWriter));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await LoadAsync(doc.Id))!.Attributes.Should().NotContainKey("owner");
    }
}
