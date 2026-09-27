using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using RetrievalService.Domain.Ports;
using RetrievalService.Tests.Grpc;
using Pb = Knowledge.Contracts.Grpc.Retrieval.V1;

namespace RetrievalService.Tests.Features.Search;

// FR-04, FR-05, NFR-09, SC-01, 計画 ADR-0086 決定 1・§結果, [[IADR-0417]] 追記 1 (#1636):
// **gRPC `AttributeValues/ListValues` の本文の利用者文脈は、許可集合の中継者（既定 `bff`）が運んだときだけ信じる**
// ことを、実 Kestrel ＋ 本物の JwtBearer（`GrpcKestrelFactory`）で固定する。
//
// 🔴 陰性（BFF 以外の `platform-service` の主体・接頭辞/大小文字の変種・`azp` の食い違い・人のトークン）と
//   陽性対照（BFF）を**同じ器・同じ索引の点**で対にする。
// 🔴 拒否された呼び出しでは**スコープ解決も呼ばれない**（`ResolvedFor` に名乗った利用者が現れない）ことも見る。
// 🔴 変異試験（実測は仕様書）: `EnsureTrustedRelay` の呼び出しを落とす／許可集合の照合を接頭辞一致に変えると、
//   それぞれ `BFF以外の_platform_service_の主体が利用者文脈を付けるとPERMISSION_DENIED` ／
//   `クライアント識別の接頭辞や大小文字の変種は信じない` が落ちる。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class AttributeValuesTrustedRelayTests
{
    private const string Trusted = "bff";
    private readonly GrpcKestrelFactory _factory;

    public AttributeValuesTrustedRelayTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Metadata Bearer(string token) => new() { { "Authorization", $"Bearer {token}" } };

    // 実 Keycloak のサービスアカウントの形。realm の `bff` は `profile` を持つので、実トークンは
    // `preferred_username = service-account-bff` と `azp=bff` の両方を持つ。
    private static string ServiceAccountToken(string clientId) =>
        GrpcKestrelFactory.IssueToken($"service-account-{clientId}", [PlatformAuthPolicies.ServiceRole], azp: clientId);

    // `profile` を持たない機械クライアントの形（利用者名なし・`azp` だけ）。
    private static string AzpOnlyToken(string clientId) =>
        GrpcKestrelFactory.IssueToken(Guid.NewGuid().ToString("D"), [PlatformAuthPolicies.ServiceRole],
            azp: clientId, withUsername: false);

    private Pb.AttributeValues.AttributeValuesClient Grpc() => new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    private static Pb.ListValuesRequest As(string userId, string key = "dept") => new()
    {
        Key = key,
        User = new Pb.UserContext { UserId = userId, Action = "read", UserAttributes = { ["role"] = "admin" } },
    };

    // 固有の部門値を持つ点を 1 つ入れ、権威スコープ（＝名乗った利用者で解決されるもの）をその点だけに向ける。
    // 🔴 器の解決器は利用者を問わず `Authoritative` を返す —— 「名乗った利用者のスコープで値が返る」ことの模型である。
    private async Task<string> SeedAsync()
    {
        var dept = $"relay-{Guid.NewGuid():N}"[..14];
        await _factory.Index.UpsertAsync(new ChunkPayload(
            Guid.NewGuid(), Guid.NewGuid(), $"doc:{dept}", "制限文書", new float[1536], "s3://b/x.md",
            new Dictionary<string, string> { ["dept"] = dept }, []));
        _factory.Authoritative = new AccessScopeResponse("owner", [new AttributeFilter("dept", [dept])], Granted: true);
        return dept;
    }

    private async Task ShouldBeDeniedAsync(string token, string because)
    {
        await SeedAsync();
        var victim = $"victim-{Guid.NewGuid():N}";
        var act = async () => await Grpc().ListValuesAsync(As(victim), Bearer(token), cancellationToken: Ct);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied, because);
        _factory.ResolvedFor.Should().NotContain(r => r.UserId == victim, "拒否した呼び出しでスコープを解決しない: " + because);
    }

    // AC-5（陽性対照）: BFF の実トークンの形（`service-account-bff` と `azp=bff`）で、利用者として値が返る。
    [Fact]
    public async Task BFFの実トークンの形なら従来どおり利用者として値を引ける()
    {
        var dept = await SeedAsync();
        var user = $"owner-{Guid.NewGuid():N}";

        var resp = await Grpc().ListValuesAsync(As(user), Bearer(ServiceAccountToken(Trusted)), cancellationToken: Ct);

        resp.Values.Should().Contain(dept);
        _factory.ResolvedFor.Should().Contain(r => r.UserId == user, "名乗った利用者で自分でスコープを解決する");
    }

    // AC-5（陽性対照）: 利用者名だけ（`azp` なし）・`azp` だけ（利用者名なし）の形でも同じ。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BFFの利用者名だけやazpだけの形でも利用者として値を引ける(bool usernameOnly)
    {
        var dept = await SeedAsync();
        var token = usernameOnly
            ? GrpcKestrelFactory.IssueToken($"service-account-{Trusted}", [PlatformAuthPolicies.ServiceRole])
            : AzpOnlyToken(Trusted);

        var resp = await Grpc().ListValuesAsync(As($"owner-{Guid.NewGuid():N}"), Bearer(token), cancellationToken: Ct);

        resp.Values.Should().Contain(dept);
    }

    // AC-1: 🔴 BFF 以外の `platform-service` の主体が利用者文脈を名乗ると PERMISSION_DENIED。
    // 同じ点・同じ形の要求で BFF には返ることを先に確かめる（対照）。
    [Theory]
    [InlineData("ai-stock-trading-llm-caller")]
    [InlineData("aianalysis-service")]
    [InlineData("mcp-server")]
    [InlineData("document-service")]
    [InlineData("retrieval-service")]
    [InlineData("graph-service")]
    public async Task BFF以外の_platform_service_の主体が利用者文脈を付けるとPERMISSION_DENIED(string clientId)
    {
        var dept = await SeedAsync();
        (await Grpc().ListValuesAsync(As($"owner-{Guid.NewGuid():N}"), Bearer(ServiceAccountToken(Trusted)), cancellationToken: Ct))
            .Values.Should().Contain(dept, "対照: 同じ点が BFF には返る");

        await ShouldBeDeniedAsync(ServiceAccountToken(clientId), clientId);
        await ShouldBeDeniedAsync(AzpOnlyToken(clientId), clientId + "（azp だけの形）");
    }

    // AC-2: 🔴 クライアント識別の接頭辞・大小文字の変種は別のクライアントである（序数一致）。
    [Theory]
    [InlineData("bff-x")]
    [InlineData("bf")]
    [InlineData("xbff")]
    [InlineData("BFF")]
    [InlineData("Bff")]
    public async Task クライアント識別の接頭辞や大小文字の変種は信じない(string variant)
    {
        await ShouldBeDeniedAsync(ServiceAccountToken(variant), variant);
        await ShouldBeDeniedAsync(AzpOnlyToken(variant), variant + "（azp だけの形）");
        await ShouldBeDeniedAsync(
            GrpcKestrelFactory.IssueToken($"service-account-{variant}", [PlatformAuthPolicies.ServiceRole]),
            variant + "（利用者名だけの形）");
    }

    // AC-3: 🔴 利用者名が `service-account-bff` でも `azp` が別なら別のクライアント（`azp` が第一）。
    [Fact]
    public async Task 利用者名がBFFでもazpが別なら信じない()
    {
        var spoofed = GrpcKestrelFactory.IssueToken($"service-account-{Trusted}", [PlatformAuthPolicies.ServiceRole],
            azp: "ai-stock-trading-llm-caller");

        await ShouldBeDeniedAsync(spoofed, "azp の食い違い");
    }

    // AC-4: 🔴 人のトークンは `azp=bff`（BFF のセッションの利用者トークンの形）と `platform-service` を持っていても利用者文脈を運べない。
    [Fact]
    public async Task 人のトークンはazpがBFFでも利用者文脈を運べない()
    {
        var human = GrpcKestrelFactory.IssueToken("carol-relay", [PlatformAuthPolicies.ServiceRole], azp: Trusted);

        await ShouldBeDeniedAsync(human, "人のトークン");
    }

    // 空の key の早期 return は判定の後にある（信頼しない呼び出し元に「通る形」を残さない）。
    [Fact]
    public async Task 信頼しない呼び出し元は空のkeyでもPERMISSION_DENIED()
    {
        var act = async () => await Grpc().ListValuesAsync(
            As($"victim-{Guid.NewGuid():N}", key: ""), Bearer(ServiceAccountToken("mcp-server")), cancellationToken: Ct);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    // AC-6: `user` の無い要求は呼び出し元を問わず INVALID_ARGUMENT（機械の視野の照会を新設しない＝広げない）。
    [Theory]
    [InlineData("ai-stock-trading-llm-caller")]
    [InlineData(Trusted)]
    public async Task 利用者文脈の無い要求は呼び出し元を問わずINVALID_ARGUMENT(string clientId)
    {
        var act = async () => await Grpc().ListValuesAsync(
            new Pb.ListValuesRequest { Key = "dept" }, Bearer(ServiceAccountToken(clientId)), cancellationToken: Ct);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }
}
