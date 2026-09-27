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

// FR-05, FR-18, NFR-09, SC-05, 計画 ADR-0086 決定 1・§結果, ADR-0063 決定 3, [[IADR-0410]] 追記 1 (#1636):
// **gRPC `DocumentTagWrite/AddTag` の本文の利用者文脈（とロール）は、許可集合の中継者（既定 `graph-service`）が運んだときだけ信じる**
// ことを、実 Kestrel ＋ 本物の JwtBearer（`GrpcKestrelFactory`）で固定する。
//
// 🔴 陰性（graph-service 以外の `platform-service` の主体・接頭辞/大小文字の変種・`azp` の食い違い・人のトークン）と
//   陽性対照（graph-service）を**同じ器・同じ文書**で対にする —— 「拒否された」だけでは器が壊れているのか判定が効いているのか区別できない。
// 🔴 拒否された呼び出しでは**文書が変わらない**（版が進まない）ことも見る —— status だけを見ると「書いてから拒否する」実装も緑になる。
// 🔴 変異試験（実測は仕様書）: `EnsureTrustedRelay` の呼び出しを落とす／許可集合の照合を接頭辞一致に変えると、
//   それぞれ `graph_service以外の_platform_service_の主体が利用者文脈を付けるとPERMISSION_DENIED` ／
//   `クライアント識別の接頭辞や大小文字の変種は信じない` が落ちる。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class DocumentTagWriteTrustedRelayTests
{
    private const string Trusted = "graph-service";
    private readonly GrpcKestrelFactory _factory;

    public DocumentTagWriteTrustedRelayTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
        // ［2026-09-27 / #1636 段 2］管理者かは認可サービスが答える（本文の `user_roles` は読まれない）。
        // 陽性対照の承認者 `operator` を管理者と答えさせる。陰性の `victim-*` は器の既定（NotAdmin）のまま。
        _factory.ApproverRoles.States["operator"] = ApproverAdminState.Admin;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Metadata Bearer(string token) => new() { { "Authorization", $"Bearer {token}" } };

    // 実 Keycloak のサービスアカウントの形。`profile` を持つ client は `preferred_username = service-account-<clientId>` も付く。
    private static string ServiceAccountToken(string clientId) =>
        GrpcKestrelFactory.IssueToken($"service-account-{clientId}", [PlatformAuthPolicies.ServiceRole], azp: clientId);

    // realm の `graph-service` の実形（既定スコープが `roles` だけ ⇒ `preferred_username` が無く `azp` だけ）。
    private static string AzpOnlyToken(string clientId) =>
        GrpcKestrelFactory.IssueToken(Guid.NewGuid().ToString("D"), [PlatformAuthPolicies.ServiceRole],
            azp: clientId, withUsername: false);

    private DocumentTagWrite.DocumentTagWriteClient Grpc() => new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    private static AddTagRequest As(Guid documentId, string tag, string userId, params string[] roles) => new()
    {
        DocumentId = documentId.ToString(),
        TagName = tag,
        UserId = userId,
        Action = "write",
        UserRoles = { roles },
    };

    // タグを辞書へ 1 つ、所有者の無い組織文書（取り込み文書の形 ＝ ②管理者の枝でしか書けない）を 1 つ入れる。
    private async Task<(Guid DocumentId, string Tag)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var tag = $"tag-{Guid.NewGuid():N}";
        db.Tags.Add(Tag.Create(tag));
        var doc = Document.CreateNormalized(
            Guid.NewGuid(), $"組織文書 {Guid.NewGuid():N}", "storage://b/x",
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, hasBody: false);
        db.Documents.Add(doc);
        await db.SaveChangesAsync(Ct);
        return (doc.Id, tag);
    }

    private async Task<int> VersionOfAsync(Guid documentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        return (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == documentId, Ct)).Version;
    }

    // 🔴 管理者の上書きを名乗る（`user_roles=["platform-admin"]`）。拒否され、文書が変わらないことを見る。
    private async Task ShouldBeDeniedAsync(string token, string because)
    {
        var (documentId, tag) = await SeedAsync();
        var act = async () => await Grpc().AddTagAsync(
            As(documentId, tag, $"victim-{Guid.NewGuid():N}", PlatformAuthPolicies.AdminRole),
            Bearer(token), cancellationToken: Ct);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied, because);
        (await VersionOfAsync(documentId)).Should().Be(1, "拒否した呼び出しで文書を書かない: " + because);
    }

    // AC-5（陽性対照）: graph-service の実トークンの形（利用者名なし・`azp` あり）で、従来どおり管理者の承認が反映される。
    [Fact]
    public async Task graph_serviceの実トークンの形なら従来どおり反映される()
    {
        var (documentId, tag) = await SeedAsync();

        var resp = await Grpc().AddTagAsync(
            As(documentId, tag, "operator", PlatformAuthPolicies.AdminRole), Bearer(AzpOnlyToken(Trusted)), cancellationToken: Ct);

        resp.Result.Should().Be(TagWriteResult.Applied);
        (await VersionOfAsync(documentId)).Should().Be(2, "対照: 書けたときは版が進む（下の陰性の『版が 1 のまま』が意味を持つ）");
    }

    // AC-5（陽性対照）: `service-account-graph-service` の利用者名の形（`azp` なし・あり）でも同じ。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task graph_serviceのサービスアカウントの利用者名の形でも反映される(bool withAzp)
    {
        var (documentId, tag) = await SeedAsync();
        var token = GrpcKestrelFactory.IssueToken($"service-account-{Trusted}", [PlatformAuthPolicies.ServiceRole],
            azp: withAzp ? Trusted : null);

        var resp = await Grpc().AddTagAsync(
            As(documentId, tag, "operator", PlatformAuthPolicies.AdminRole), Bearer(token), cancellationToken: Ct);

        resp.Result.Should().Be(TagWriteResult.Applied);
    }

    // AC-1 / AC-9: 🔴 graph-service 以外の `platform-service` の主体が利用者文脈と管理者ロールを名乗ると PERMISSION_DENIED。
    // 同じ形の要求が graph-service には通ることを先に確かめる（対照）。
    [Theory]
    [InlineData("ai-stock-trading-llm-caller")]
    [InlineData("bff")]
    [InlineData("mcp-server")]
    [InlineData("document-service")]
    [InlineData("retrieval-service")]
    [InlineData("aianalysis-service")]
    public async Task graph_service以外の_platform_service_の主体が利用者文脈を付けるとPERMISSION_DENIED(string clientId)
    {
        var (documentId, tag) = await SeedAsync();
        (await Grpc().AddTagAsync(As(documentId, tag, "operator", PlatformAuthPolicies.AdminRole),
            Bearer(ServiceAccountToken(Trusted)), cancellationToken: Ct))
            .Result.Should().Be(TagWriteResult.Applied, "対照: 同じ形の要求が graph-service には通る");

        await ShouldBeDeniedAsync(ServiceAccountToken(clientId), clientId);
        await ShouldBeDeniedAsync(AzpOnlyToken(clientId), clientId + "（azp だけの形）");
    }

    // AC-2: 🔴 クライアント識別の接頭辞・大小文字の変種は別のクライアントである（序数一致）。
    [Theory]
    [InlineData("graph-service-x")]
    [InlineData("graph-servic")]
    [InlineData("xgraph-service")]
    [InlineData("GRAPH-SERVICE")]
    [InlineData("Graph-Service")]
    public async Task クライアント識別の接頭辞や大小文字の変種は信じない(string variant)
    {
        await ShouldBeDeniedAsync(ServiceAccountToken(variant), variant);
        await ShouldBeDeniedAsync(AzpOnlyToken(variant), variant + "（azp だけの形）");
        await ShouldBeDeniedAsync(
            GrpcKestrelFactory.IssueToken($"service-account-{variant}", [PlatformAuthPolicies.ServiceRole]),
            variant + "（利用者名だけの形）");
    }

    // AC-3: 🔴 利用者名が `service-account-graph-service` でも `azp` が別なら別のクライアント（`azp` が第一）。
    [Fact]
    public async Task 利用者名がgraph_serviceでもazpが別なら信じない()
    {
        var spoofed = GrpcKestrelFactory.IssueToken($"service-account-{Trusted}", [PlatformAuthPolicies.ServiceRole],
            azp: "ai-stock-trading-llm-caller");

        await ShouldBeDeniedAsync(spoofed, "azp の食い違い");
    }

    // AC-4: 🔴 人のトークンは `azp=graph-service` と `platform-service` を持っていても利用者文脈を運べない。
    [Fact]
    public async Task 人のトークンはazpがgraph_serviceでも利用者文脈を運べない()
    {
        var human = GrpcKestrelFactory.IssueToken("carol-relay", [PlatformAuthPolicies.ServiceRole], azp: Trusted);

        await ShouldBeDeniedAsync(human, "人のトークン");
    }

    // 判定はタグ名の検証より前にある（信頼しない呼び出し元に「検証の答え」を返さない）。
    [Fact]
    public async Task 信頼しない呼び出し元は空のタグ名でもPERMISSION_DENIED()
    {
        var act = async () => await Grpc().AddTagAsync(
            As(Guid.NewGuid(), "", $"victim-{Guid.NewGuid():N}"), Bearer(ServiceAccountToken("mcp-server")), cancellationToken: Ct);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    // AC-6: `user_id` の無い要求は呼び出し元を問わず INVALID_ARGUMENT（機械の視野の書き込みを新設しない＝広げない）。
    [Theory]
    [InlineData("ai-stock-trading-llm-caller")]
    [InlineData(Trusted)]
    public async Task 利用者文脈の無い要求は呼び出し元を問わずINVALID_ARGUMENT(string clientId)
    {
        var (documentId, tag) = await SeedAsync();

        var act = async () => await Grpc().AddTagAsync(
            As(documentId, tag, "", PlatformAuthPolicies.AdminRole), Bearer(ServiceAccountToken(clientId)), cancellationToken: Ct);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        (await VersionOfAsync(documentId)).Should().Be(1);
    }
}
