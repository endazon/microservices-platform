using AwesomeAssertions;
using GraphService.Domain;
using GraphService.Tests.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Pb = Knowledge.Contracts.Grpc.Graph.V1;

namespace GraphService.Tests.Features.Graph;

// FR-04, FR-05, FR-17, NFR-09, 計画 ADR-0086 決定 1・§結果, ADR-0034 決定 1, [[IADR-0410]] 追記 1 (#1636):
// **gRPC `GraphNeighbors/ExpandNeighbors` の本文の利用者文脈は、許可集合の中継者（既定 `retrieval-service`）が運んだときだけ信じる**
// ことを、実 Kestrel ＋ 本物の JwtBearer（`GrpcKestrelFactory`）で固定する。
//
// 🔴 陰性（retrieval-service 以外の `platform-service` の主体・接頭辞/大小文字の変種・`azp` の食い違い・人のトークン）と
//   陽性対照（retrieval-service）を**同じ器・同じ辺**で対にする。
// 🔴 拒否された呼び出しでは**スコープ解決も呼ばれない**（`ResolvedFor` に名乗った利用者が現れない）ことも見る ——
//   status だけを見ると「解決してから捨てる」実装も緑になる。
// 🔴 変異試験（実測は仕様書）: `EnsureTrustedRelay` の呼び出しを落とす／許可集合の照合を接頭辞一致に変えると、
//   それぞれ `retrieval_service以外の_platform_service_の主体が利用者文脈を付けるとPERMISSION_DENIED` ／
//   `クライアント識別の接頭辞や大小文字の変種は信じない` が落ちる。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class GraphNeighborsTrustedRelayTests
{
    private const string Trusted = "retrieval-service";
    private readonly GrpcKestrelFactory _factory;

    public GraphNeighborsTrustedRelayTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
        _factory.ResolvedFor.Clear();
        _factory.ScopeFor = (user, _) => new AccessScopeResponse(user.UserId, [], true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Metadata Bearer(string token) => new() { { "Authorization", $"Bearer {token}" } };

    // 実 Keycloak のサービスアカウントの形。`profile` を持つ client は `preferred_username = service-account-<clientId>` も付く。
    private static string ServiceAccountToken(string clientId) =>
        GrpcKestrelFactory.IssueToken($"service-account-{clientId}", [PlatformAuthPolicies.ServiceRole], azp: clientId);

    // realm の `retrieval-service` の実形（既定スコープが `roles` だけ ⇒ `preferred_username` が無く `azp` だけ）。
    private static string AzpOnlyToken(string clientId) =>
        GrpcKestrelFactory.IssueToken(Guid.NewGuid().ToString("D"), [PlatformAuthPolicies.ServiceRole],
            azp: clientId, withUsername: false);

    private Pb.GraphNeighbors.GraphNeighborsClient Grpc() => new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    private static Pb.ExpandNeighborsRequest As(Guid documentId, string userId) => new()
    {
        DocumentId = documentId.ToString(),
        Hops = 1,
        User = new Pb.UserContext { UserId = userId, Action = "read", UserAttributes = { ["clearance"] = "secret" } },
    };

    // A→B の 1 辺を張る。器の解決器は利用者を問わず全許可を返す —— 「名乗った利用者のスコープで辺が返る」ことの模型である。
    private async Task<Guid> SeedAsync()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        await _factory.SeedAsync(db =>
        {
            db.EdgeTypes.Add(EdgeType.Create($"t-{typeId:N}", EdgeTypeLayer.Core, false));
            foreach (var (id, name) in new[] { (a, "A"), (b, "B") })
                db.Documents.Add(GraphDocument.Create(id, name,
                    new Dictionary<string, string> { ["confidentiality"] = "internal" }, null, DateTimeOffset.UtcNow));
            db.Edges.Add(Edge.Create(a, b, typeId, false, EdgeProvenance.Auto));
            return Task.CompletedTask;
        });
        return a;
    }

    private async Task ShouldBeDeniedAsync(string token, string because)
    {
        var a = await SeedAsync();
        var victim = $"victim-{Guid.NewGuid():N}";
        var act = async () => await Grpc().ExpandNeighborsAsync(As(a, victim), Bearer(token), cancellationToken: Ct);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied, because);
        _factory.ResolvedFor.Should().NotContain(r => r.User.UserId == victim, "拒否した呼び出しでスコープを解決しない: " + because);
    }

    // AC-5（陽性対照）: retrieval-service の実トークンの形（利用者名なし・`azp` あり）で、利用者として辺が返る。
    [Fact]
    public async Task retrieval_serviceの実トークンの形なら従来どおり利用者として近傍を引ける()
    {
        var a = await SeedAsync();
        var user = $"owner-{Guid.NewGuid():N}";

        var resp = await Grpc().ExpandNeighborsAsync(As(a, user), Bearer(AzpOnlyToken(Trusted)), cancellationToken: Ct);

        resp.Found.Should().BeTrue();
        resp.Edges.Should().ContainSingle();
        _factory.ResolvedFor.Should().Contain(r => r.User.UserId == user, "名乗った利用者で自分でスコープを解決する");
    }

    // AC-5（陽性対照）: `service-account-retrieval-service` の利用者名の形（`azp` なし・あり）でも同じ。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task retrieval_serviceのサービスアカウントの利用者名の形でも利用者として近傍を引ける(bool withAzp)
    {
        var a = await SeedAsync();
        var token = GrpcKestrelFactory.IssueToken($"service-account-{Trusted}", [PlatformAuthPolicies.ServiceRole],
            azp: withAzp ? Trusted : null);

        var resp = await Grpc().ExpandNeighborsAsync(As(a, $"owner-{Guid.NewGuid():N}"), Bearer(token), cancellationToken: Ct);

        resp.Edges.Should().ContainSingle();
    }

    // AC-1: 🔴 retrieval-service 以外の `platform-service` の主体が利用者文脈を名乗ると PERMISSION_DENIED。
    // 同じ辺が retrieval-service には返ることを先に確かめる（対照）。
    [Theory]
    [InlineData("ai-stock-trading-llm-caller")]
    [InlineData("bff")]
    [InlineData("mcp-server")]
    [InlineData("document-service")]
    [InlineData("graph-service")]
    [InlineData("aianalysis-service")]
    public async Task retrieval_service以外の_platform_service_の主体が利用者文脈を付けるとPERMISSION_DENIED(string clientId)
    {
        var a = await SeedAsync();
        (await Grpc().ExpandNeighborsAsync(As(a, $"owner-{Guid.NewGuid():N}"), Bearer(ServiceAccountToken(Trusted)), cancellationToken: Ct))
            .Edges.Should().ContainSingle("対照: 同じ辺が retrieval-service には返る");

        await ShouldBeDeniedAsync(ServiceAccountToken(clientId), clientId);
        await ShouldBeDeniedAsync(AzpOnlyToken(clientId), clientId + "（azp だけの形）");
    }

    // AC-2: 🔴 クライアント識別の接頭辞・大小文字の変種は別のクライアントである（序数一致）。
    [Theory]
    [InlineData("retrieval-service-x")]
    [InlineData("retrieval-servic")]
    [InlineData("xretrieval-service")]
    [InlineData("RETRIEVAL-SERVICE")]
    [InlineData("Retrieval-Service")]
    public async Task クライアント識別の接頭辞や大小文字の変種は信じない(string variant)
    {
        await ShouldBeDeniedAsync(ServiceAccountToken(variant), variant);
        await ShouldBeDeniedAsync(AzpOnlyToken(variant), variant + "（azp だけの形）");
        await ShouldBeDeniedAsync(
            GrpcKestrelFactory.IssueToken($"service-account-{variant}", [PlatformAuthPolicies.ServiceRole]),
            variant + "（利用者名だけの形）");
    }

    // AC-3: 🔴 利用者名が `service-account-retrieval-service` でも `azp` が別なら別のクライアント（`azp` が第一）。
    [Fact]
    public async Task 利用者名がretrieval_serviceでもazpが別なら信じない()
    {
        var spoofed = GrpcKestrelFactory.IssueToken($"service-account-{Trusted}", [PlatformAuthPolicies.ServiceRole],
            azp: "ai-stock-trading-llm-caller");

        await ShouldBeDeniedAsync(spoofed, "azp の食い違い");
    }

    // AC-4: 🔴 人のトークンは `azp=retrieval-service` と `platform-service` を持っていても利用者文脈を運べない。
    [Fact]
    public async Task 人のトークンはazpがretrieval_serviceでも利用者文脈を運べない()
    {
        var human = GrpcKestrelFactory.IssueToken("carol-relay", [PlatformAuthPolicies.ServiceRole], azp: Trusted);

        await ShouldBeDeniedAsync(human, "人のトークン");
    }

    // AC-6: `user` の無い要求は呼び出し元を問わず INVALID_ARGUMENT（機械の視野の近傍展開を新設しない＝広げない）。
    [Theory]
    [InlineData("ai-stock-trading-llm-caller")]
    [InlineData(Trusted)]
    public async Task 利用者文脈の無い要求は呼び出し元を問わずINVALID_ARGUMENT(string clientId)
    {
        var a = await SeedAsync();

        var act = async () => await Grpc().ExpandNeighborsAsync(
            new Pb.ExpandNeighborsRequest { DocumentId = a.ToString(), Hops = 1 },
            Bearer(ServiceAccountToken(clientId)), cancellationToken: Ct);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // 🔴 利用者文脈を持たない辺の型の重みは、許可集合の外の主体にも従来どおり返る（変えていない）。
    [Fact]
    public async Task 辺の型の重みは許可集合の外の主体にも返る()
    {
        await SeedAsync();

        var resp = await Grpc().ListEdgeTypeWeightsAsync(
            new Pb.ListEdgeTypeWeightsRequest(), Bearer(ServiceAccountToken("mcp-server")), cancellationToken: Ct);

        resp.Weights.Should().NotBeEmpty();
    }
}
