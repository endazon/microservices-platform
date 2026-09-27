using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using DocumentService.Tests.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Grpc.Document.V1;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Tests.Features.Documents;

// NFR-09, FR-06, FR-19, UC-03, 計画 ADR-0119 決定 3, ADR-0086 決定 1, ADR-0034 決定 9, ADR-0056 (#1614):
// **読み取りの全ての口が認証を要し、gRPC 面でも個人資料を所有者・共有先以外へ返さない**ことを、
// **実 Kestrel ＋ 本物の JwtBearer**（`GrpcKestrelFactory`。署名つきの JWT を検証する）で固定する。
//
// 🔴 `TestAuthHandler` の器では 401 が観測できない（常に認証する）。ここは**本物の認証の門**を通す。
// 🔴 陰性（401 / UNAUTHENTICATED / found=false）は陽性対照（同じ口・資格情報あり）と対にする。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class DocumentReadAuthenticationTests
{
    private const string ServiceSubject = "service-account-bff";
    private readonly GrpcKestrelFactory _factory;

    public DocumentReadAuthenticationTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ServiceToken() =>
        GrpcKestrelFactory.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole]);

    private static string UserToken(string user, params string[] roles) =>
        GrpcKestrelFactory.IssueToken(user, roles);

    private static Metadata Bearer(string token) => new() { { "Authorization", $"Bearer {token}" } };

    private DocumentRead.DocumentReadClient Grpc() => new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    private HttpClient Rest(string? token = null)
    {
        var http = new HttpClient { BaseAddress = new Uri(_factory.HttpAddress) };
        if (token is not null)
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private async Task<Document> SeedAsync(Dictionary<string, string> attributes)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var doc = Document.Create($"auth-{Guid.NewGuid():N}", null, null, attributes);
        db.Documents.Add(doc);
        await db.SaveChangesAsync(Ct);
        return doc;
    }

    private static Dictionary<string, string> Org() => new()
    {
        ["confidentiality"] = "internal",
        ["doc_scope"] = "organization",
    };

    private static Dictionary<string, string> PrivateNote(string owner) => new()
    {
        ["confidentiality"] = "restricted",
        ["doc_scope"] = "private-note",
        ["owner"] = owner,
    };

    private static UserContext As(string user) => new() { UserId = user, Action = "read" };

    // ── AC-1: REST の 5 口 ─────────────────────────────────────────────────

    public static TheoryData<string> RestReadRoutes() =>
    [
        "/documents",
        "/documents/page",
        "/documents/{id}",
        "/documents/{id}/versions",
        "/documents/{id}/versions/1",
    ];

    // 🔴 トークンが無ければ 401。**同じ口に利用者のトークンを付けると 200**（陽性対照）。
    [Theory]
    [MemberData(nameof(RestReadRoutes))]
    public async Task REST_の読み取りはトークンが無ければ401で_利用者のトークンがあれば200(string route)
    {
        var org = await SeedAsync(Org());
        var path = route.Replace("{id}", org.Id.ToString("D"), StringComparison.Ordinal);

        (await Rest().GetAsync(path, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, path);
        (await Rest(UserToken("reader-rest")).GetAsync(path, Ct)).StatusCode.Should().Be(HttpStatusCode.OK, path);
    }

    // 署名の合わないトークンも 401（「何か付いていれば通る」ではない）。
    [Fact]
    public async Task REST_の読み取りは偽のトークンでも401()
    {
        var forged = UserToken("forger")[..^4] + "AAAA";
        (await Rest(forged).GetAsync("/documents", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // 健全性の口は匿名のまま（AC-10）。
    [Fact]
    public async Task 健全性の口は匿名のまま()
    {
        (await Rest().GetAsync("/health/live", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // AC-2 / AC-4（REST・本物の JWT）: 他人・機械には個人資料が返らず、所有者には返る。
    [Fact]
    public async Task REST_の個人資料は所有者のトークンにだけ返り_他人と機械には404()
    {
        var note = await SeedAsync(PrivateNote("alice-rest"));
        var path = $"/documents/{note.Id}";

        (await Rest(UserToken("alice-rest")).GetAsync(path, Ct)).StatusCode.Should().Be(HttpStatusCode.OK, "陽性対照");
        (await Rest(UserToken("mallory-rest", PlatformAuthPolicies.AdminRole)).GetAsync(path, Ct)).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "他人（管理者でも）");
        (await Rest(ServiceToken()).GetAsync(path, Ct)).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "機械クライアント");

        var machineList = (await Rest(ServiceToken()).GetFromJsonAsync<List<DocumentDto>>("/documents", Ct))!;
        machineList.Should().NotContain(d => d.Id == note.Id);
    }

    // ── AC-1: gRPC の 4 rpc ────────────────────────────────────────────────

    [Fact]
    public async Task gRPC_の読み取りはトークンが無ければ4口ともUNAUTHENTICATED()
    {
        var org = await SeedAsync(Org());
        var id = org.Id.ToString("D");
        var client = Grpc();

        var calls = new Func<Task>[]
        {
            async () => await client.ListDocumentsAsync(new ListDocumentsRequest(), cancellationToken: Ct),
            async () => await client.GetDocumentAsync(new GetDocumentRequest { Id = id }, cancellationToken: Ct),
            async () => await client.ListVersionsAsync(new ListVersionsRequest { DocumentId = id }, cancellationToken: Ct),
            async () => await client.GetVersionAsync(new GetVersionRequest { DocumentId = id, Version = 1 },
                cancellationToken: Ct),
        };
        foreach (var call in calls)
            (await call.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);

        // 陽性対照: 同じ口に s2s トークンを付けると通る。
        (await client.GetDocumentAsync(new GetDocumentRequest { Id = id }, headers: Bearer(ServiceToken()),
            cancellationToken: Ct)).Found.Should().BeTrue();
    }

    // ── AC-2 / AC-3 / AC-4: gRPC の主体 ─────────────────────────────────────

    // 🔴 他人（本文の利用者文脈）には一覧に出ず個別 3 口は found=false。所有者には全部返る。
    [Fact]
    public async Task gRPC_の個人資料は本文の利用者が所有者のときだけ返る()
    {
        var note = await SeedAsync(PrivateNote("alice-grpc"));
        var id = note.Id.ToString("D");
        var client = Grpc();
        var s2s = Bearer(ServiceToken());

        async Task<(bool InList, bool Get, bool Versions, bool Version)> ReadAs(UserContext? user)
        {
            var list = await client.ListDocumentsAsync(new ListDocumentsRequest { User = user }, s2s, cancellationToken: Ct);
            var get = await client.GetDocumentAsync(new GetDocumentRequest { Id = id, User = user }, s2s, cancellationToken: Ct);
            var versions = await client.ListVersionsAsync(
                new ListVersionsRequest { DocumentId = id, User = user }, s2s, cancellationToken: Ct);
            var version = await client.GetVersionAsync(
                new GetVersionRequest { DocumentId = id, Version = 1, User = user }, s2s, cancellationToken: Ct);
            return (list.Documents.Any(d => d.Id == id), get.Found, versions.Found, version.Found);
        }

        (await ReadAs(As("alice-grpc"))).Should().Be((true, true, true, true), "陽性対照: 所有者には返る");
        (await ReadAs(As("mallory-grpc"))).Should().Be((false, false, false, false), "他人には存在ごと見えない");
        (await ReadAs(null)).Should().Be((false, false, false, false), "利用者文脈なし＝呼び出し元サービス自身（機械）");
        (await ReadAs(As("service-account-alice-grpc"))).Should().Be((false, false, false, false),
            "本文の利用者名が機械の形なら機械として扱う");
    }

    // AC-4: 利用者文脈なし（機械）は組織文書を読める。
    [Fact]
    public async Task gRPC_の機械の主体は組織文書を読める()
    {
        var org = await SeedAsync(Org());
        var resp = await Grpc().GetDocumentAsync(new GetDocumentRequest { Id = org.Id.ToString("D") },
            headers: Bearer(ServiceToken()), cancellationToken: Ct);
        resp.Found.Should().BeTrue();
    }

    // AC-7: 「利用者が分からない」を機械の主体へ畳まない。
    [Fact]
    public async Task gRPC_の利用者文脈の_user_id_が空なら_INVALID_ARGUMENT()
    {
        var org = await SeedAsync(Org());
        var act = async () => await Grpc().GetDocumentAsync(
            new GetDocumentRequest { Id = org.Id.ToString("D"), User = new UserContext { UserId = "" } },
            headers: Bearer(ServiceToken()), cancellationToken: Ct);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }
}
