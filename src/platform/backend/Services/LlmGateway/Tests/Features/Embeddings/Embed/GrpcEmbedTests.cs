using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using LlmGateway.Features.Embeddings.Embed;
using Microsoft.AspNetCore.Authorization;
using Platform.Shared.Contracts.Dtos;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Grpc;

namespace LlmGateway.Tests.Features.Embeddings.Embed;

// FR-02, FR-03, FR-05, NFR-09, NFR-16, ADR-0013, ADR-0016, ADR-0017, ADR-0029, ADR-0075,
// IADR-0379, IADR-0397 (#1255): 埋め込みの gRPC 面を**実 Kestrel の h2c ポート**で往復し、
// s2s トークンの検証と越境判定（fail-closed）が gRPC 経路でも保たれることを固定する。
//
// 陽性対照（T-S-01）と陰性対照（T-S-02 / T-S-03）を同じ器で対にする —— 「拒否された」だけでは
// 器が壊れているのか認可が効いているのか区別できない。
[Trait("TestKind", "Integration")]
public class GrpcEmbedTests : IClassFixture<GrpcKestrelFactory>
{
    private const string ServiceSubject = "service-account-retrieval-service";
    private readonly GrpcKestrelFactory _factory;

    public GrpcEmbedTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    private static Metadata Bearer(string token) => new() { { "Authorization", $"Bearer {token}" } };

    private Pb.LlmEmbedding.LlmEmbeddingClient PlainClient() =>
        new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    private static string ServiceToken() =>
        GrpcKestrelFactory.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole]);

    // 呼び出し側の共通部品（CreatePlatformChannel = 平文 h2c ＋ s2s CallCredentials）を実際に通す。
    private sealed class FixedTokenProvider(string token) : IServiceTokenProvider
    {
        public ValueTask<string> GetTokenAsync(CancellationToken ct) => ValueTask.FromResult(token);
    }

    // T-S-01: 陽性対照。s2s トークン（platform-service）を CallCredentials で付けた h2c チャネルで
    // 往復し、既定の外部経路（voyage / 1024 次元）のベクトルと embedded=true を得る。
    //
    // 🔴 `confidentiality` は明示する。proto3 の string の既定は空文字であり、
    // `SensitivityClasses.Parse("")` は **restricted（安全側）** へ倒す（REST の null と同じ扱い）。
    // 空のまま Index で送ると越境判定でティアA 固定になり fail-closed になる —— それは正しい挙動で
    // あって陽性対照ではない（実測でここを踏んだ。T-S-13 が同じ事実を陰性側から固定する）。
    [Fact]
    public async Task Embed_over_h2c_with_service_token_returns_vector()
    {
        using var channel = GrpcClientExtensions.CreatePlatformChannel(
            _factory.GrpcAddress, new FixedTokenProvider(ServiceToken()));
        var client = new Pb.LlmEmbedding.LlmEmbeddingClient(channel);

        var resp = await client.EmbedAsync(
            new Pb.EmbedRequest { Text = "本文", Confidentiality = "public", Purpose = Pb.EmbedPurpose.Index },
            cancellationToken: TestContext.Current.CancellationToken);

        resp.Embedded.Should().BeTrue();
        resp.Vector.Should().HaveCount(1024);
        resp.Dimensions.Should().Be(1024);
        resp.Model.Should().Be("voyage-3.5");
        resp.Collection.Should().Be("knowledge_chunks_voyage_3_5");
        resp.Retryable.Should().BeFalse();
    }

    // T-S-02: 陰性対照。資格情報が無ければ UNAUTHENTICATED。
    [Fact]
    public async Task Embed_without_credentials_is_unauthenticated()
    {
        var act = async () => await PlainClient().EmbedAsync(
            new Pb.EmbedRequest { Text = "本文" }, cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.Unauthenticated);
    }

    // T-S-03: 🔴 陰性対照。**利用者のトークン（管理者であっても）を転送しても通らない** ——
    // s2s の面は platform-service ロールだけを通す。これが緩むと利用者トークンの転送
    // （confused deputy）が成立する。IADR-0379 決定 4 を機械で守る唯一の点である。
    [Fact]
    public async Task Embed_with_forwarded_admin_user_token_is_permission_denied()
    {
        var userToken = GrpcKestrelFactory.IssueToken("admin-user", [PlatformAuthPolicies.AdminRole]);

        var act = async () => await PlainClient().EmbedAsync(
            new Pb.EmbedRequest { Text = "本文" }, headers: Bearer(userToken),
            cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.PermissionDenied);
    }

    // T-S-04: REST と gRPC は同じ判定器（EmbedUseCase）を呼ぶ —— 同じ入力に同じ答えを返す。
    // （REST が同じプロセスの HTTP/1.1 ポートで応えることが、ポート維持の 2 つ目の証明でもある。）
    [Fact]
    public async Task Rest_and_grpc_embed_the_same_input_identically()
    {
        var grpc = await PlainClient().EmbedAsync(
            new Pb.EmbedRequest { Text = "本文", Confidentiality = "public", Purpose = Pb.EmbedPurpose.Index },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        using var http = new HttpClient { BaseAddress = new Uri(_factory.HttpAddress) };
        var restResp = await http.PostAsJsonAsync("/embed",
            new EmbedApiRequest("本文", "public", Platform.Shared.Contracts.Dtos.EmbedPurpose.Index),
            TestContext.Current.CancellationToken);
        restResp.EnsureSuccessStatusCode();
        var rest = (await restResp.Content.ReadFromJsonAsync<EmbedApiResponse>(
            TestContext.Current.CancellationToken))!;

        grpc.Embedded.Should().Be(rest.Embedded);
        grpc.Dimensions.Should().Be(rest.Dimensions);
        grpc.Model.Should().Be(rest.Model);
        grpc.Collection.Should().Be(rest.Collection);
        grpc.Retryable.Should().Be(rest.Retryable);
        grpc.Vector.Should().BeEquivalentTo(rest.Vector);
        grpc.RoutingReason.Should().Be(rest.RoutingReason);
    }

    // T-S-07: 🔴 proto3 に null は無い（IADR-0397 決定 3）。EMBED_PURPOSE_UNSPECIFIED（既定 0）は
    // REST の DTO 既定である **Index** として routing されなければならない。
    //
    // 観測点として confidential を選ぶ —— Index なら越境判定でティアA 固定になり、
    // セルフホストが既定無効なので **fail-closed（embedded=false）** になる。もし誤って Query として
    // 扱われると public 相当となり、**機密文書の本文が外部経路（voyage）へ送られる**。
    // すなわちこの写し漏れは「例外」ではなく **egress の違反**として現れる。
    [Fact]
    public async Task Unspecified_purpose_routes_as_index_not_query()
    {
        var unspecified = await PlainClient().EmbedAsync(
            new Pb.EmbedRequest { Text = "機密本文", Confidentiality = "confidential" },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        unspecified.Embedded.Should().BeFalse("UNSPECIFIED は Index として越境判定され fail-closed になる");
        unspecified.RoutingReason.Should().Contain("Index");
        unspecified.Vector.Should().BeEmpty();

        // 陽性対照: 同じ機密区分でも Query は既定外部経路へ固定される（ADR-0016）。
        // これが無いと「常に false を返す壊れ方」と区別できない。
        var query = await PlainClient().EmbedAsync(
            new Pb.EmbedRequest { Text = "機密本文", Confidentiality = "confidential", Purpose = Pb.EmbedPurpose.Query },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        query.Embedded.Should().BeTrue();
        query.RoutingReason.Should().Contain("Query");

        // 明示的な INDEX は UNSPECIFIED と同じ結果になる（写しが Index を指していることの直接の証明）。
        var index = await PlainClient().EmbedAsync(
            new Pb.EmbedRequest { Text = "機密本文", Confidentiality = "confidential", Purpose = Pb.EmbedPurpose.Index },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        index.Embedded.Should().Be(unspecified.Embedded);
        index.RoutingReason.Should().Be(unspecified.RoutingReason);
    }

    // T-S-11: 縮退（越境拒否）は RpcException ではなく embedded=false の**応答**で返る
    // （REST の 200 ＋ Embedded=false と同値）。エラーにすると呼び出し側は
    // 「ポリシーが働いた」と「後段が壊れた」を区別できなくなる。
    [Fact]
    public async Task Egress_denied_is_a_response_not_an_error()
    {
        var resp = await PlainClient().EmbedAsync(
            new Pb.EmbedRequest { Text = "機密本文", Confidentiality = "restricted", Purpose = Pb.EmbedPurpose.Index },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        resp.Embedded.Should().BeFalse();
        resp.Retryable.Should().BeFalse("機密区分による拒否は恒久であり再試行しない");
        resp.RoutingReason.Should().Contain("fail-closed");
    }

    // T-S-13: 🔴 proto3 の string に null は無い。未指定の `confidentiality`（空文字）は
    // REST の `null` と**同じく restricted（安全側）**へ倒れなければならない（SensitivityClasses.Parse）。
    // ここが逆へ倒れると、機密区分を送り忘れた呼び出し元の本文が外部経路へ出る。
    // 陽性対照: 明示した "restricted" と結果が一致すること（＝空文字が restricted と同じ扱い）。
    [Fact]
    public async Task Empty_confidentiality_falls_back_to_restricted()
    {
        var empty = await PlainClient().EmbedAsync(
            new Pb.EmbedRequest { Text = "本文", Purpose = Pb.EmbedPurpose.Index },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        var restricted = await PlainClient().EmbedAsync(
            new Pb.EmbedRequest { Text = "本文", Confidentiality = "restricted", Purpose = Pb.EmbedPurpose.Index },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        empty.Embedded.Should().BeFalse();
        empty.RoutingReason.Should().Be(restricted.RoutingReason);
    }

    // T-S-12: gRPC 用ポートは HTTP/2 専用である。HTTP/1.1 の要求は受け付けない。
    // 陽性対照: 同じ要求が HTTP/1.1 のポートでは 200 を返す（＝ gRPC を有効にしても 8080 相当は残る）。
    [Fact]
    public async Task Grpc_port_rejects_http11_while_http_port_still_serves()
    {
        using var http11 = new HttpClient
        {
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        var onGrpcPort = await http11.GetAsync(
            $"{_factory.GrpcAddress}/health/live", TestContext.Current.CancellationToken);
        onGrpcPort.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "h2c 専用ポートは HTTP/1.1 の要求を処理しない（Http1AndHttp2 ではない）");

        var onHttpPort = await http11.GetAsync(
            $"{_factory.HttpAddress}/health/live", TestContext.Current.CancellationToken);
        onHttpPort.StatusCode.Should().Be(HttpStatusCode.OK,
            "gRPC リスナを足しても HTTP/1.1 のポート（REST・/health/*）は消えない");
    }

    // T-S-10: 構造の門。gRPC サービス型が ServiceCaller ポリシーを宣言していること
    // （属性が外れると T-S-02 / T-S-03 が落ちるが、どの層で外れたかを名指しするためにここでも固定する）。
    [Fact]
    public void Grpc_service_declares_service_caller_policy()
    {
        var attr = typeof(LlmEmbeddingGrpcService)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>().SingleOrDefault();

        attr.Should().NotBeNull();
        attr!.Policy.Should().Be(PlatformAuthPolicies.ServiceCaller);
    }
}
