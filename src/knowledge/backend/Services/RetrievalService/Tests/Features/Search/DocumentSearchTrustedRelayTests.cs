using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using RetrievalService.Domain.Ports;
using RetrievalService.Tests.Grpc;
using Pb = Knowledge.Contracts.Grpc.Retrieval.V1;

namespace RetrievalService.Tests.Features.Search;

// FR-19, FR-03, FR-04, NFR-09, 計画 ADR-0086 決定 1, ADR-0119 決定 3, ADR-0034 決定 1, [[IADR-0426]] 追記 1 (#1635):
// **gRPC `DocumentSearch/Search` の本文の利用者文脈は、許可集合の中継者（既定 `aianalysis-service`）が運んだときだけ信じる**
// ことを、実 Kestrel ＋ 本物の JwtBearer（`GrpcKestrelFactory`）で固定する。
//
// 🔴 陰性（AI 分析以外の `platform-service` の主体・接頭辞/大小文字の変種・`azp` の食い違い・人のトークン）と
//   陽性対照（AI 分析）を**同じ器・同じ索引の点**で対にする —— 「拒否された」だけでは器が壊れているのか
//   判定が効いているのか区別できない。
// 🔴 拒否された呼び出しでは**スコープ解決も呼ばれない**（`ResolvedFor` に名乗った利用者が現れない）ことも見る ——
//   status だけを見ると「解決してから捨てる」実装も緑になる。
// 🔴 変異試験（実測は仕様書）: `EnsureTrustedRelay` の呼び出しを落とす／許可集合の照合を接頭辞一致に変えると、
//   それぞれ `AI分析以外の_platform_service_の主体が利用者文脈を付けるとPERMISSION_DENIED` ／
//   `クライアント識別の接頭辞や大小文字の変種は信じない` が落ちる。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class DocumentSearchTrustedRelayTests
{
    private const string Trusted = "aianalysis-service";
    private readonly GrpcKestrelFactory _factory;

    public DocumentSearchTrustedRelayTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Metadata Bearer(string token) => new() { { "Authorization", $"Bearer {token}" } };

    // 実 Keycloak のサービスアカウントの形。`profile` を持つ client は `preferred_username = service-account-<clientId>` も付く。
    private static string ServiceAccountToken(string clientId) =>
        GrpcKestrelFactory.IssueToken($"service-account-{clientId}", [PlatformAuthPolicies.ServiceRole], azp: clientId);

    // realm の `aianalysis-service` の実形（既定スコープが `roles` だけ ⇒ `preferred_username` が無く `azp` だけ）。
    private static string AzpOnlyToken(string clientId) =>
        GrpcKestrelFactory.IssueToken(Guid.NewGuid().ToString("D"), [PlatformAuthPolicies.ServiceRole],
            azp: clientId, withUsername: false);

    private Pb.DocumentSearch.DocumentSearchClient Grpc() => new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    private static Pb.SearchRequest As(string userId, string query = "検索") => new()
    {
        Query = query,
        TopK = 50,
        User = new Pb.UserContext { UserId = userId, Action = "read", UserAttributes = { ["role"] = "admin" } },
    };

    // 本文つきのチャンクを 1 点入れ、権威スコープ（＝名乗った利用者で解決されるもの）をその点だけに向ける。
    // 🔴 器の解決器は利用者を問わず `Authoritative` を返す —— 「名乗った利用者のスコープで本文が返る」ことの模型である
    //   （実物では個人資料の分岐が名乗った利用者で開く。個人資料の述語そのものは `PrivateNoteSearchExposureTests` の範囲）。
    private async Task<(string Dept, Guid DocumentId)> SeedPrivateNoteChunkAsync()
    {
        var dept = $"relay-{Guid.NewGuid():N}"[..14];
        var documentId = Guid.NewGuid();
        await _factory.Index.UpsertAsync(new ChunkPayload(
            Guid.NewGuid(), documentId, $"doc:{dept}", "検索 他人の個人資料の本文", new float[1536], "s3://b/x.md",
            new Dictionary<string, string> { ["dept"] = dept }, [], null, true));
        _factory.Authoritative = new AccessScopeResponse(
            "owner", [new AttributeFilter("dept", [dept])], Granted: true);
        return (dept, documentId);
    }

    private async Task ShouldBeDeniedAsync(string token, string userId, string because)
    {
        var act = async () => await Grpc().SearchAsync(As(userId), Bearer(token), cancellationToken: Ct);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied, because);
        _factory.ResolvedFor.Should().NotContain(r => r.UserId == userId, "拒否した呼び出しでスコープを解決しない: " + because);
    }

    // AC-5（陽性対照）: AI 分析の実トークンの形（利用者名なし・`azp` あり）で、利用者として検索でき本文が返る。
    [Fact]
    public async Task AI分析の実トークンの形なら従来どおり利用者として検索できる()
    {
        var (_, documentId) = await SeedPrivateNoteChunkAsync();
        var user = $"owner-{Guid.NewGuid():N}";

        var resp = await Grpc().SearchAsync(As(user), Bearer(AzpOnlyToken(Trusted)), cancellationToken: Ct);

        resp.Results.Should().ContainSingle(r => r.DocumentId == documentId.ToString());
        _factory.ResolvedFor.Should().Contain(r => r.UserId == user, "名乗った利用者で自分でスコープを解決する");
    }

    // AC-5（陽性対照）: `service-account-aianalysis-service` の利用者名の形（`azp` なし・あり）でも同じ。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AI分析のサービスアカウントの利用者名の形でも利用者として検索できる(bool withAzp)
    {
        var (_, documentId) = await SeedPrivateNoteChunkAsync();
        var user = $"owner-{Guid.NewGuid():N}";
        var token = GrpcKestrelFactory.IssueToken($"service-account-{Trusted}", [PlatformAuthPolicies.ServiceRole],
            azp: withAzp ? Trusted : null);

        var resp = await Grpc().SearchAsync(As(user), Bearer(token), cancellationToken: Ct);

        resp.Results.Should().ContainSingle(r => r.DocumentId == documentId.ToString());
    }

    // AC-1: 🔴 AI 分析以外の `platform-service` の主体が利用者文脈（管理者の属性つき）を名乗ると PERMISSION_DENIED。
    // 同じ点・同じ利用者で AI 分析には返ることを先に確かめる（対照）。
    [Theory]
    [InlineData("ai-stock-trading-llm-caller")]
    [InlineData("bff")]
    [InlineData("mcp-server")]
    [InlineData("document-service")]
    [InlineData("retrieval-service")]
    [InlineData("graph-service")]
    public async Task AI分析以外の_platform_service_の主体が利用者文脈を付けるとPERMISSION_DENIED(string clientId)
    {
        var (_, documentId) = await SeedPrivateNoteChunkAsync();
        var control = $"owner-{Guid.NewGuid():N}";
        (await Grpc().SearchAsync(As(control), Bearer(ServiceAccountToken(Trusted)), cancellationToken: Ct))
            .Results.Should().Contain(r => r.DocumentId == documentId.ToString(), "対照: 同じ点が AI 分析には返る");

        await ShouldBeDeniedAsync(ServiceAccountToken(clientId), $"victim-{Guid.NewGuid():N}", clientId);
        await ShouldBeDeniedAsync(AzpOnlyToken(clientId), $"victim-{Guid.NewGuid():N}", clientId + "（azp だけの形）");
    }

    // AC-2: 🔴 クライアント識別の接頭辞・大小文字の変種は別のクライアントである（序数一致）。
    [Theory]
    [InlineData("aianalysis-service-x")]
    [InlineData("aianalysis-servic")]
    [InlineData("xaianalysis-service")]
    [InlineData("AIANALYSIS-SERVICE")]
    [InlineData("Aianalysis-Service")]
    public async Task クライアント識別の接頭辞や大小文字の変種は信じない(string variant)
    {
        await SeedPrivateNoteChunkAsync();

        await ShouldBeDeniedAsync(ServiceAccountToken(variant), $"victim-{Guid.NewGuid():N}", variant);
        await ShouldBeDeniedAsync(AzpOnlyToken(variant), $"victim-{Guid.NewGuid():N}", variant + "（azp だけの形）");
        await ShouldBeDeniedAsync(
            GrpcKestrelFactory.IssueToken($"service-account-{variant}", [PlatformAuthPolicies.ServiceRole]),
            $"victim-{Guid.NewGuid():N}", variant + "（利用者名だけの形）");
    }

    // AC-3: 🔴 利用者名が `service-account-aianalysis-service` でも `azp` が別なら別のクライアント（`azp` が第一）。
    [Fact]
    public async Task 利用者名がAI分析でもazpが別なら信じない()
    {
        await SeedPrivateNoteChunkAsync();
        var spoofed = GrpcKestrelFactory.IssueToken($"service-account-{Trusted}", [PlatformAuthPolicies.ServiceRole],
            azp: "ai-stock-trading-llm-caller");

        await ShouldBeDeniedAsync(spoofed, $"victim-{Guid.NewGuid():N}", "azp の食い違い");
    }

    // AC-4: 🔴 人のトークンは `azp=aianalysis-service` と `platform-service` を持っていても利用者文脈を運べない。
    [Fact]
    public async Task 人のトークンはazpがAI分析でも利用者文脈を運べない()
    {
        await SeedPrivateNoteChunkAsync();
        var human = GrpcKestrelFactory.IssueToken("carol-relay", [PlatformAuthPolicies.ServiceRole], azp: Trusted);

        await ShouldBeDeniedAsync(human, $"victim-{Guid.NewGuid():N}", "人のトークン");
    }

    // 空の query の早期 return は判定の後にある（信頼しない呼び出し元に「通る形」を残さない）。
    [Fact]
    public async Task 信頼しない呼び出し元は空のqueryでもPERMISSION_DENIED()
    {
        var act = async () => await Grpc().SearchAsync(
            As($"victim-{Guid.NewGuid():N}", query: ""), Bearer(ServiceAccountToken("mcp-server")), cancellationToken: Ct);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    // AC-6: `user` の無い要求は呼び出し元を問わず INVALID_ARGUMENT（機械の視野の検索を新設しない＝広げない）。
    [Theory]
    [InlineData("ai-stock-trading-llm-caller")]
    [InlineData(Trusted)]
    public async Task 利用者文脈の無い要求は呼び出し元を問わずINVALID_ARGUMENT(string clientId)
    {
        var act = async () => await Grpc().SearchAsync(
            new Pb.SearchRequest { Query = "検索", TopK = 10 }, Bearer(ServiceAccountToken(clientId)), cancellationToken: Ct);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }
}
