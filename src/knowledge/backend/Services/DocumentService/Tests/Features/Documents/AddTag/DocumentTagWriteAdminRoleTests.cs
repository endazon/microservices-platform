using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;
using DocumentService.Tests.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Knowledge.Contracts.Grpc.Document.V1;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Tests.Features.Documents.AddTag;

// FR-05, FR-18, NFR-09, SC-05, 計画 ADR-0088 決定 1, ADR-0063 決定 3, ADR-0036 D-08, [[IADR-0410]] 追記 2 (#1636):
// **gRPC のタグの反映は本文の `user_roles` を信じない。承認者が管理者かは認可サービスが答える**ことを、
// 実 Kestrel ＋ 本物の JwtBearer で、許可集合の中継者（graph-service）の実トークンの形から固定する。
//
// 🔴 AC-1 が本体: 中継者が `user_roles=["platform-admin"]` を運んでも、認可サービスが「管理者ではない」と答えれば②は通らない。
//   変異「本文のロールを再び信じる」（`request.UserRoles.Contains(AdminRole)` へ戻す）で落ちる。
// 🔴 認可サービスを引けないときは UNAVAILABLE（「書けない」へ畳まない）。変異「Unknown を NotAdmin へ畳む」で落ちる。
// 🔴 所有者・個人資料では認可サービスへ問わない（往復を増やさない・②が及ばない）ことも観測する。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class DocumentTagWriteAdminRoleTests
{
    private readonly GrpcKestrelFactory _factory;

    public DocumentTagWriteAdminRoleTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // realm の `graph-service` の実形（利用者名なし・`azp` だけ）。許可集合の既定の中継者である。
    private static Metadata GraphService() => new()
    {
        { "Authorization", "Bearer " + GrpcKestrelFactory.IssueToken(
            Guid.NewGuid().ToString("D"), [PlatformAuthPolicies.ServiceRole], azp: "graph-service", withUsername: false) },
    };

    private DocumentTagWrite.DocumentTagWriteClient Grpc() => new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    private static AddTagRequest As(Guid documentId, string tag, string userId, params string[] roles) => new()
    {
        DocumentId = documentId.ToString(),
        TagName = tag,
        UserId = userId,
        Action = "write",
        UserRoles = { roles },
    };

    private async Task<(Guid DocumentId, string Tag)> SeedAsync(Dictionary<string, string> attributes)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var tag = $"tag-{Guid.NewGuid():N}";
        db.Tags.Add(Tag.Create(tag));
        var doc = Document.CreateNormalized(
            Guid.NewGuid(), $"文書 {Guid.NewGuid():N}", "storage://b/x", attributes, hasBody: false);
        db.Documents.Add(doc);
        await db.SaveChangesAsync(Ct);
        return (doc.Id, tag);
    }

    // 取り込み文書の形（所有者なし ＝ ②管理者の枝でしか書けない）。
    private Task<(Guid DocumentId, string Tag)> SeedOrgDocumentAsync()
        => SeedAsync(new Dictionary<string, string> { ["confidentiality"] = "internal" });

    private async Task<int> VersionOfAsync(Guid documentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        return (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == documentId, Ct)).Version;
    }

    private static string NewUser(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    // AC-1: 🔴 中継者が本文で管理者を名乗っても、認可サービスが管理者でないと答えれば②は通らない（文書も変わらない）。
    [Fact]
    public async Task 本文のuser_rolesに管理者があっても認可サービスが管理者でないと答えれば反映されない()
    {
        var (documentId, tag) = await SeedOrgDocumentAsync();
        var claimed = NewUser("claimed-admin");
        _factory.ApproverRoles.States[claimed] = ApproverAdminState.NotAdmin;

        var resp = await Grpc().AddTagAsync(
            As(documentId, tag, claimed, PlatformAuthPolicies.AdminRole), GraphService(), cancellationToken: Ct);

        resp.Result.Should().Be(TagWriteResult.NotWritable, "本文のロールは判定に用いない");
        (await VersionOfAsync(documentId)).Should().Be(1);
        _factory.ApproverRoles.Asked.Should().Contain(claimed, "管理者かは認可サービスに問うた");
    }

    // AC-2: 本文に `user_roles` が無くても、認可サービスが管理者と答えれば所有者のいない組織文書へ反映される（陽性対照）。
    [Fact]
    public async Task 本文にロールが無くても認可サービスが管理者と答えれば反映される()
    {
        var (documentId, tag) = await SeedOrgDocumentAsync();
        var admin = NewUser("admin");
        _factory.ApproverRoles.States[admin] = ApproverAdminState.Admin;

        var resp = await Grpc().AddTagAsync(As(documentId, tag, admin), GraphService(), cancellationToken: Ct);

        resp.Result.Should().Be(TagWriteResult.Applied);
        (await VersionOfAsync(documentId)).Should().Be(2);
    }

    // AC-3: 🔴 認可サービスを引けなければ、所有者でない承認者の要求は UNAVAILABLE（「書けない」へ畳まない・文書は変わらない）。
    [Fact]
    public async Task 認可サービスを引けなければ所有者でない承認者の要求はUNAVAILABLE()
    {
        var (documentId, tag) = await SeedOrgDocumentAsync();
        var approver = NewUser("approver");
        _factory.ApproverRoles.States[approver] = ApproverAdminState.Unknown;

        var act = async () => await Grpc().AddTagAsync(
            As(documentId, tag, approver, PlatformAuthPolicies.AdminRole), GraphService(), cancellationToken: Ct);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unavailable);
        (await VersionOfAsync(documentId)).Should().Be(1);
    }

    // AC-4: 所有者の要求は認可サービスを呼ばずに反映される（引けない状態でも通る）。
    [Fact]
    public async Task 所有者の要求は認可サービスを呼ばずに反映される()
    {
        var owner = NewUser("owner");
        var (documentId, tag) = await SeedAsync(new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
            ["owner"] = owner,
        });
        _factory.ApproverRoles.States[owner] = ApproverAdminState.Unknown;

        var resp = await Grpc().AddTagAsync(As(documentId, tag, owner), GraphService(), cancellationToken: Ct);

        resp.Result.Should().Be(TagWriteResult.Applied);
        _factory.ApproverRoles.Asked.Should().NotContain(owner, "①所有者で書けるときは問わない");
    }

    // AC-5: 個人資料へは、認可サービスが管理者と答えても書けず（#1629）、認可サービスも呼ばれない。
    [Fact]
    public async Task 個人資料へは管理者でも反映されず認可サービスも呼ばれない()
    {
        var (documentId, tag) = await SeedAsync(new Dictionary<string, string>
        {
            ["confidentiality"] = "restricted",
            ["doc_scope"] = "private-note",
            ["owner"] = NewUser("alice"),
        });
        var admin = NewUser("admin");
        _factory.ApproverRoles.States[admin] = ApproverAdminState.Admin;

        var resp = await Grpc().AddTagAsync(
            As(documentId, tag, admin, PlatformAuthPolicies.AdminRole), GraphService(), cancellationToken: Ct);

        resp.Result.Should().Be(TagWriteResult.NotWritable);
        _factory.ApproverRoles.Asked.Should().NotContain(admin, "②は個人資料に及ばないので問わない");
    }
}
