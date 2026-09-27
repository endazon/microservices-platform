using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using DocumentService.Tests.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Knowledge.Contracts.Grpc.Document.V1;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Tests.Features.Documents;

// FR-19, FR-06, NFR-09, 計画 ADR-0086 決定 1, ADR-0119 決定 3, ADR-0034 決定 9, [[IADR-0476]] 追記 (#1628):
// **gRPC `DocumentRead` の本文の利用者文脈は、許可集合の中継者（既定 `bff`）が運んだときだけ信じる**ことを、
// 実 Kestrel ＋ 本物の JwtBearer（`GrpcKestrelFactory`）で固定する。
//
// 🔴 陰性（BFF 以外の `platform-service` の主体）と陽性対照（BFF）を同じ器・同じ文書で対にする ——
//   「拒否された」だけでは器が壊れているのか判定が効いているのか区別できない。
// 🔴 変異試験: `DocumentReadGrpcService.PrincipalOf` から `TrustsUserContextFrom` の確認を落とすと
//   `BFF以外の_platform_service_の主体が他人の利用者文脈を付けると4口ともPERMISSION_DENIED` が落ちる（実測は仕様書）。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class DocumentReadTrustedRelayTests
{
    private readonly GrpcKestrelFactory _factory;

    public DocumentReadTrustedRelayTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Metadata Bearer(string token) => new() { { "Authorization", $"Bearer {token}" } };

    // 実 Keycloak のサービスアカウントと同じ形（`preferred_username = service-account-<clientId>`・`azp = clientId`）。
    private static string ServiceAccountToken(string clientId) =>
        GrpcKestrelFactory.IssueToken($"service-account-{clientId}", [PlatformAuthPolicies.ServiceRole], azp: clientId);

    private DocumentRead.DocumentReadClient Grpc() => new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    private async Task<Document> SeedAsync(Dictionary<string, string> attributes)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var doc = Document.Create($"relay-{Guid.NewGuid():N}", null, null, attributes);
        db.Documents.Add(doc);
        await db.SaveChangesAsync(Ct);
        return doc;
    }

    private static Dictionary<string, string> PrivateNote(string owner) => new()
    {
        ["confidentiality"] = "restricted",
        ["doc_scope"] = "private-note",
        ["owner"] = owner,
    };

    private static Dictionary<string, string> Org() => new()
    {
        ["confidentiality"] = "internal",
        ["doc_scope"] = "organization",
    };

    private static UserContext As(string user) => new() { UserId = user, Action = "read" };

    private Func<Task>[] AllFour(string token, string id, UserContext? user)
    {
        var client = Grpc();
        var s2s = Bearer(token);
        return
        [
            async () => await client.ListDocumentsAsync(new ListDocumentsRequest { User = user }, s2s, cancellationToken: Ct),
            async () => await client.GetDocumentAsync(new GetDocumentRequest { Id = id, User = user }, s2s, cancellationToken: Ct),
            async () => await client.ListVersionsAsync(
                new ListVersionsRequest { DocumentId = id, User = user }, s2s, cancellationToken: Ct),
            async () => await client.GetVersionAsync(
                new GetVersionRequest { DocumentId = id, Version = 1, User = user }, s2s, cancellationToken: Ct),
        ];
    }

    // AC-3（陽性対照）: BFF（`azp=bff` のサービスアカウント）が所有者の利用者文脈を運ぶと、4 口とも個人資料が返る。
    [Fact]
    public async Task BFFが所有者の利用者文脈を運ぶと個人資料が返る()
    {
        var note = await SeedAsync(PrivateNote("alice-relay"));
        var id = note.Id.ToString("D");
        var client = Grpc();
        var s2s = Bearer(ServiceAccountToken("bff"));

        (await client.ListDocumentsAsync(new ListDocumentsRequest { User = As("alice-relay") }, s2s, cancellationToken: Ct))
            .Documents.Should().Contain(d => d.Id == id);
        (await client.GetDocumentAsync(new GetDocumentRequest { Id = id, User = As("alice-relay") }, s2s,
            cancellationToken: Ct)).Found.Should().BeTrue();
        (await client.ListVersionsAsync(new ListVersionsRequest { DocumentId = id, User = As("alice-relay") }, s2s,
            cancellationToken: Ct)).Found.Should().BeTrue();
        (await client.GetVersionAsync(new GetVersionRequest { DocumentId = id, Version = 1, User = As("alice-relay") },
            s2s, cancellationToken: Ct)).Found.Should().BeTrue();

        // 他人の利用者文脈なら返らない（従来どおり。BFF を信じることは「誰でも読める」ではない）。
        (await client.GetDocumentAsync(new GetDocumentRequest { Id = id, User = As("mallory-relay") }, s2s,
            cancellationToken: Ct)).Found.Should().BeFalse();
    }

    // AC-1 / AC-6: 🔴 BFF 以外の `platform-service` の主体（AST の LLM 呼び出し用・realm の他のサービス）が
    // 所有者の利用者文脈を名乗っても、4 口とも PERMISSION_DENIED で個人資料は返らない。
    [Theory]
    [InlineData("ai-stock-trading-llm-caller")]
    [InlineData("retrieval-service")]
    [InlineData("mcp-server")]
    [InlineData("document-service")]
    public async Task BFF以外の_platform_service_の主体が他人の利用者文脈を付けると4口ともPERMISSION_DENIED(string clientId)
    {
        var note = await SeedAsync(PrivateNote("alice-relay-deny"));

        foreach (var call in AllFour(ServiceAccountToken(clientId), note.Id.ToString("D"), As("alice-relay-deny")))
            (await call.Should().ThrowAsync<RpcException>()).Which.StatusCode
                .Should().Be(StatusCode.PermissionDenied, clientId);
    }

    // AC-2: 同じ主体が利用者文脈を**付けなければ**機械の主体として読める（拒否は一律ではない）。
    // 組織文書は返り、個人資料は一覧に出ず個別は found=false（ADR-0034 決定 9）。
    [Fact]
    public async Task BFF以外の主体も利用者文脈なしなら機械として組織文書を読み_個人資料は読めない()
    {
        var org = await SeedAsync(Org());
        var note = await SeedAsync(PrivateNote("alice-relay-machine"));
        var client = Grpc();
        var s2s = Bearer(ServiceAccountToken("ai-stock-trading-llm-caller"));

        (await client.GetDocumentAsync(new GetDocumentRequest { Id = org.Id.ToString("D") }, s2s,
            cancellationToken: Ct)).Found.Should().BeTrue("対照: 呼び出し元サービス自身としては読める");

        var list = await client.ListDocumentsAsync(new ListDocumentsRequest(), s2s, cancellationToken: Ct);
        list.Documents.Should().Contain(d => d.Id == org.Id.ToString("D"));
        list.Documents.Should().NotContain(d => d.Id == note.Id.ToString("D"));
        (await client.GetDocumentAsync(new GetDocumentRequest { Id = note.Id.ToString("D") }, s2s,
            cancellationToken: Ct)).Found.Should().BeFalse();
    }

    // AC-4: `azp=bff` を持つ**人**のトークンは、`platform-service` を持っていても利用者文脈を運べない
    // （`azp` は BFF のセッションの利用者トークンにも付く。機械であることを併せて求める）。
    [Fact]
    public async Task azpがbffでも人のトークンは利用者文脈を運べない()
    {
        var note = await SeedAsync(PrivateNote("alice-relay-human"));
        var humanWithBffAzp = GrpcKestrelFactory.IssueToken("carol-relay", [PlatformAuthPolicies.ServiceRole], azp: "bff");

        foreach (var call in AllFour(humanWithBffAzp, note.Id.ToString("D"), As("alice-relay-human")))
            (await call.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    // 利用者識別子が空の文脈は、信頼しない呼び出し元にはまず PERMISSION_DENIED（空かどうかを教えない）。
    // 信頼する呼び出し元なら従来どおり INVALID_ARGUMENT（`DocumentReadAuthenticationTests`）。
    [Fact]
    public async Task 信頼しない呼び出し元の空の利用者文脈はPERMISSION_DENIED()
    {
        var org = await SeedAsync(Org());
        var act = async () => await Grpc().GetDocumentAsync(
            new GetDocumentRequest { Id = org.Id.ToString("D"), User = new UserContext { UserId = "" } },
            headers: Bearer(ServiceAccountToken("graph-service")), cancellationToken: Ct);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }
}
