using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using Grpc.Core;
using Grpc.Net.Client;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Grpc.Document.V1;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Tests.Features.Documents;

// FR-05, FR-06, FR-19, NFR-09, UC-03, 計画 ADR-0121 決定 4・5, ADR-0119 決定 3, ADR-0056, ADR-0088 (#1615):
// **内容の ABAC の門が開いた器**で、読み取りの全ての口（REST 4 口・`/documents/page`・gRPC 4 rpc）が
// 認可サービスの分岐で判定することを固定する。
//
// 🔴 陰性（一覧に居ない・404・`found=false`）は、同じ口・同じ文書で陽性対照（属性の合う主体には返る）と対にする。
// 分岐は dev seed を入れた認可サービスの応答の期待値（`OwnerReadSeedScopes`）を入力にする（bob = clearance internal）。
[Trait("TestKind", "Integration")]
public class DocumentReadContentAbacEndpointTests(ContentAbacOpenWebApplicationFactory factory)
    : IClassFixture<ContentAbacOpenWebApplicationFactory>
{
    private const string KbWriter = "service-account-ai-stock-trading-kb-writer";

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];

    private HttpClient ClientAs(string user, string? clientId = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-user");
        if (clientId is not null)
            client.DefaultRequestHeaders.Add(TestAuthHandler.ClientIdHeader, clientId);
        return client;
    }

    private async Task<Document> SeedAsync(Dictionary<string, string> attributes, string? title = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var doc = Document.Create(title ?? $"doc-{Guid.NewGuid():N}", null, null, attributes);
        db.Documents.Add(doc);
        await db.SaveChangesAsync(Ct);
        return doc;
    }

    private static Dictionary<string, string> Org(string confidentiality, string? owner = null, string? tag = null)
    {
        var attributes = new Dictionary<string, string>
        {
            ["confidentiality"] = confidentiality,
            ["doc_scope"] = "organization",
        };
        if (owner is not null) attributes["owner"] = owner;
        if (tag is not null) attributes["abac_probe"] = tag;
        return attributes;
    }

    // 利用者に seed の分岐を与える（名前だけ替えて、分岐の中の本人の名前も替える）。
    private void GrantSeedLike(string user, string seedSubject)
    {
        var branches = OwnerReadSeedScopes.BranchesOf(seedSubject)
            .Select(b => (IReadOnlyList<AttributeFilter>)b
                .Select(f => new AttributeFilter(f.Key, [.. f.AllowedValues.Select(v => v == seedSubject ? user : v)]))
                .ToList())
            .ToList();
        factory.ReadScopes.Grant(user, branches);
    }

    private async Task<(bool InList, HttpStatusCode Get, HttpStatusCode Versions, HttpStatusCode Version)> ReadAllAsync(
        HttpClient client, Guid id)
    {
        var list = (await client.GetFromJsonAsync<List<DocumentDto>>("/documents", Ct))!;
        var get = await client.GetAsync($"/documents/{id}", Ct);
        var versions = await client.GetAsync($"/documents/{id}/versions", Ct);
        var version = await client.GetAsync($"/documents/{id}/versions/1", Ct);
        return (list.Any(d => d.Id == id), get.StatusCode, versions.StatusCode, version.StatusCode);
    }

    // ── AC-2: REST の 4 口 ─────────────────────────────────────────────────────

    // T-62: 属性の合わない利用者には restricted の組織文書が一覧に出ず、個別の 3 口は 404。属性の合う組織文書は返る（陽性対照）。
    [Fact]
    public async Task REST_属性の合わない利用者には機密の組織文書が返らない()
    {
        var bob = Unique("bob");
        GrantSeedLike(bob, "bob");
        var restricted = await SeedAsync(Org("restricted", owner: Unique("carol")));
        var internalDoc = await SeedAsync(Org("internal", owner: Unique("carol")));

        var hidden = await ReadAllAsync(ClientAs(bob), restricted.Id);
        hidden.InList.Should().BeFalse();
        hidden.Get.Should().Be(HttpStatusCode.NotFound);
        hidden.Versions.Should().Be(HttpStatusCode.NotFound);
        hidden.Version.Should().Be(HttpStatusCode.NotFound);

        var shown = await ReadAllAsync(ClientAs(bob), internalDoc.Id);
        shown.InList.Should().BeTrue("陽性対照");
        shown.Get.Should().Be(HttpStatusCode.OK);
        shown.Versions.Should().Be(HttpStatusCode.OK);
        shown.Version.Should().Be(HttpStatusCode.OK);
    }

    // T-62 / AC-8: 一覧は件数によらず、要求ごとに主体あたり 1 回だけ認可サービスへ問う。
    [Fact]
    public async Task REST_一覧は文書の件数によらず主体ごとに1回だけ問い合わせる()
    {
        var bob = Unique("bob");
        GrantSeedLike(bob, "bob");
        for (var i = 0; i < 5; i++)
            await SeedAsync(Org(i % 2 == 0 ? "internal" : "restricted"));

        var before = factory.ReadScopes.CallsFor(bob);
        _ = await ClientAs(bob).GetFromJsonAsync<List<DocumentDto>>("/documents", Ct);

        factory.ReadScopes.CallsFor(bob).Should().Be(before + 1);
    }

    // ── AC-9: AST の KB の書き手 ───────────────────────────────────────────────

    // T-63: 機械の主体（`service-account-…`）は、自分が owner の写しを一覧で見つけられる。`owner=system`・`owner` の欠落・他人の文書は見えない。
    [Fact]
    public async Task REST_ASTのKBの書き手は自分の写しだけを一覧で見つけられる()
    {
        factory.ReadScopes.Grant(KbWriter, OwnerReadSeedScopes.BranchesOf(KbWriter));
        var own = await SeedAsync(Org("internal", owner: KbWriter));
        var system = await SeedAsync(Org("public", owner: "system"));
        var ownerless = await SeedAsync(Org("public"));
        var others = await SeedAsync(Org("public", owner: Unique("alice")));

        var list = (await ClientAs(KbWriter, "ai-stock-trading-kb-writer")
            .GetFromJsonAsync<List<DocumentDto>>("/documents", Ct))!.Select(d => d.Id).ToList();

        list.Should().Contain(own.Id, "陽性対照: 入れ直しは自分の写しを見つける（ADR-0119 フォローアップ 2）");
        list.Should().NotContain([system.Id, ownerless.Id, others.Id]);
    }

    // ── AC-12: /documents/page ─────────────────────────────────────────────────

    // T-64: `/page` も同じ判定を通り、切り出しの前に絞る（読めない文書でページが短くならず、カーソルで読める文書を全件辿れる）。
    [Fact]
    public async Task Page_門が開くと読める文書だけを切り出しカーソルで全件辿れる()
    {
        var bob = Unique("bob");
        GrantSeedLike(bob, "bob");
        var probe = Unique("probe");
        var readable = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var doc = await SeedAsync(Org(i % 2 == 0 ? "restricted" : "internal", tag: probe));
            if (i % 2 == 1) readable.Add(doc.Id);
        }

        var client = ClientAs(bob);
        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = $"/documents/page?limit=2&attr.abac_probe={probe}" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = (await client.GetFromJsonAsync<DocumentPageDto>(url, Ct))!;
            if (page.NextCursor is not null)
                page.Items.Should().HaveCount(2, "読めない文書で途中のページが短くならない");
            seen.AddRange(page.Items.Select(d => d.Id));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);

        seen.Should().BeEquivalentTo(readable, "restricted は 1 件も返らず、internal は全件返る");
    }

    // ── AC-2 / AC-7: gRPC の 4 rpc ──────────────────────────────────────────────

    private DocumentRead.DocumentReadClient Grpc()
    {
        var server = factory.Server;
        var channel = GrpcChannel.ForAddress(server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = server.CreateHandler() });
        return new DocumentRead.DocumentReadClient(channel);
    }

    // BFF（信頼する中継者）の s2s の形。
    private static Metadata AsBff() => new()
    {
        { TestAuthHandler.UserHeader, "service-account-bff" },
        { TestAuthHandler.ClientIdHeader, "bff" },
        { TestAuthHandler.RolesHeader, PlatformAuthPolicies.ServiceRole },
    };

    private async Task<(bool InList, bool Get, bool Versions, bool Version)> ReadAllGrpcAsync(UserContext user, Guid id)
    {
        var client = Grpc();
        var list = await client.ListDocumentsAsync(new ListDocumentsRequest { User = user }, AsBff(), cancellationToken: Ct);
        var get = await client.GetDocumentAsync(new GetDocumentRequest { Id = id.ToString(), User = user }, AsBff(), cancellationToken: Ct);
        var versions = await client.ListVersionsAsync(
            new ListVersionsRequest { DocumentId = id.ToString(), User = user }, AsBff(), cancellationToken: Ct);
        var version = await client.GetVersionAsync(
            new GetVersionRequest { DocumentId = id.ToString(), Version = 1, User = user }, AsBff(), cancellationToken: Ct);
        return (list.Documents.Any(d => d.Id == id.ToString()), get.Found, versions.Found, version.Found);
    }

    // T-65: gRPC の 4 rpc も同じ判定（読めなければ一覧から除き `found=false`）。🔴 本文の `user_attributes` は信じない ——
    // `clearance=restricted` を載せても、認可サービスの分岐（clearance=internal）で判定する。
    [Fact]
    public async Task Grpc_本文の属性を信じず属性の合わない利用者には機密の組織文書が返らない()
    {
        var bob = Unique("bob");
        GrantSeedLike(bob, "bob");
        var restricted = await SeedAsync(Org("restricted"));
        var internalDoc = await SeedAsync(Org("internal"));
        var forged = new UserContext { UserId = bob, Action = "read" };
        forged.UserAttributes.Add("clearance", "restricted");

        var hidden = await ReadAllGrpcAsync(forged, restricted.Id);
        hidden.Should().Be((false, false, false, false), "本文の clearance=restricted で広がらない");

        var shown = await ReadAllGrpcAsync(forged, internalDoc.Id);
        shown.Should().Be((true, true, true, true), "陽性対照");
    }

    // T-65: `user` の無い gRPC は呼び出し元サービスのアカウント（ここでは BFF 自身）で判定する。属性も所有も無いので何も読めない。
    [Fact]
    public async Task Grpc_利用者文脈の無い呼び出しは呼び出し元サービスのアカウントで判定する()
    {
        factory.ReadScopes.Grant("service-account-bff", [[new AttributeFilter("owner", ["service-account-bff"])]]);
        var mine = await SeedAsync(Org("restricted", owner: "service-account-bff"));
        var other = await SeedAsync(Org("public"));

        var list = await Grpc().ListDocumentsAsync(new ListDocumentsRequest(), AsBff(), cancellationToken: Ct);

        list.Documents.Select(d => d.Id).Should().Contain(mine.Id.ToString(), "陽性対照: 呼び出し元の名前で問うている");
        list.Documents.Select(d => d.Id).Should().NotContain(other.Id.ToString());
    }

    // ── 書き込みの 403 ／ 404 の「読めるか」も同じ判定点が答える（ADR-0056） ──────────────

    // T-66: 機械の主体が他人の組織文書のメタデータを書き換えようとすると、読めない文書なら 404、読める文書なら 403。
    [Fact]
    public async Task 書き込みの拒否は内容のABACで読めるかにより404と403に分かれる()
    {
        var machine = $"service-account-{Unique("writer")}";
        factory.ReadScopes.Grant(machine, [[new AttributeFilter("confidentiality", ["public"])]]);
        var unreadable = await SeedAsync(Org("restricted", owner: Unique("carol")));
        var readable = await SeedAsync(Org("public", owner: Unique("carol")));
        var client = ClientAs(machine, machine["service-account-".Length..]);
        client.DefaultRequestHeaders.Remove(TestAuthHandler.RolesHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, PlatformAuthPolicies.OperatorRole);

        var hidden = await client.DeleteAsync($"/documents/{unreadable.Id}", Ct);
        var forbidden = await client.DeleteAsync($"/documents/{readable.Id}", Ct);

        hidden.StatusCode.Should().Be(HttpStatusCode.NotFound);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden, "陽性対照: 読めるが書けない");
    }
}

// FR-05, NFR-09, 計画 ADR-0121 決定 4 (#1615): **門が閉じた器（既定）**では、機密の組織文書が機械・属性の無い利用者にも返り、
// `/documents/page` も同じく返し、認可サービスを 1 度も問わない（今日の判定のまま）。
[Trait("TestKind", "Integration")]
public class DocumentReadContentAbacClosedGateTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private const string KbWriter = "service-account-ai-stock-trading-kb-writer";

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-54
    [Fact]
    public async Task 門が閉じている間はRESTとページの口が機密の組織文書を返し認可サービスを問わない()
    {
        var probe = $"probe-{Guid.NewGuid():N}";
        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            var doc = Document.Create("restricted", null, null, new Dictionary<string, string>
            {
                ["confidentiality"] = "restricted",
                ["doc_scope"] = "organization",
                ["owner"] = "someone-else",
                ["abac_probe"] = probe,
            });
            db.Documents.Add(doc);
            await db.SaveChangesAsync(Ct);
            id = doc.Id;
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, KbWriter);
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, PlatformAuthPolicies.OperatorRole);

        (await client.GetFromJsonAsync<List<DocumentDto>>("/documents", Ct))!.Should().Contain(d => d.Id == id);
        (await client.GetAsync($"/documents/{id}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetFromJsonAsync<DocumentPageDto>($"/documents/page?attr.abac_probe={probe}", Ct))!
            .Items.Should().ContainSingle(d => d.Id == id);

        factory.ContentAbac.IsOpen.Should().BeFalse();
        factory.ReadScopes.CallsFor(KbWriter).Should().Be(0);
    }
}
