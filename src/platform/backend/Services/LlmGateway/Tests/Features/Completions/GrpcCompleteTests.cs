using System.Net.Http.Json;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using LlmGateway.Features.Completions;
using LlmGateway.Tests.Grpc;
using Microsoft.AspNetCore.Authorization;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace LlmGateway.Tests.Features.Completions;

// FR-04, FR-11, NFR-02, NFR-09, NFR-16, ADR-0010, ADR-0025, ADR-0029, ADR-0075, ADR-0076,
// IADR-0101, IADR-0104, IADR-0379, IADR-0397, IADR-0398 (#1255):
// テキスト生成の一括 rpc（`LlmCompletion/Complete`）を**実 Kestrel の h2c ポート**で往復し、
// s2s トークンの検証・越境判定・proto3 の既定値の写しが gRPC 経路でも保たれることを固定する。
//
// 陽性対照（T-S-01）と陰性対照（T-S-02 / T-S-03）を同じ器で対にする —— 「拒否された」だけでは
// 器が壊れているのか認可が効いているのか区別できない。
[Collection(SharedMeterCollection.Name)]
[Trait("TestKind", "Integration")]
public class GrpcCompleteTests
{
    private const string ServiceSubject = "service-account-aianalysis-service";
    private readonly GrpcKestrelFactory _factory;

    public GrpcCompleteTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    private static Metadata Bearer(string token) => new() { { "Authorization", $"Bearer {token}" } };

    private Pb.LlmCompletion.LlmCompletionClient PlainClient() =>
        new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    private static string ServiceToken() =>
        GrpcKestrelFactory.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole]);

    private sealed class FixedTokenProvider(string token) : IServiceTokenProvider
    {
        public ValueTask<string> GetTokenAsync(CancellationToken ct) => ValueTask.FromResult(token);
    }

    private static Pb.CompleteRequest Request(string prompt = "本文", int maxTokens = 100) => new()
    {
        Prompt = prompt,
        MaxTokens = maxTokens,
        Confidentiality = "public",
        Purpose = "default",
    };

    // T-S-01: 陽性対照。s2s トークン（platform-service）を CallCredentials で付けた h2c チャネルで
    // 往復し、送信が成立した応答を得る。**共通部品（CreatePlatformChannel）を実際に通す。**
    [Fact]
    public async Task Complete_over_h2c_with_service_token_returns_completion()
    {
        using var channel = GrpcClientExtensions.CreatePlatformChannel(
            _factory.GrpcAddress, new FixedTokenProvider(ServiceToken()));
        var client = new Pb.LlmCompletion.LlmCompletionClient(channel);

        var resp = await client.CompleteAsync(
            Request(), cancellationToken: TestContext.Current.CancellationToken);

        resp.Sent.Should().BeTrue();
        resp.Text.Should().Contain("max_tokens=100");
        resp.InputTokens.Should().Be(ScriptedLlmProvider.InputTokens);
        resp.OutputTokens.Should().Be(ScriptedLlmProvider.OutputTokens);
        resp.StopReason.Should().Be(CompletionStopReasons.EndTurn);
        resp.Model.Should().NotBeEmpty();
    }

    // T-S-02: 陰性対照。資格情報が無ければ UNAUTHENTICATED。
    [Fact]
    public async Task Complete_without_credentials_is_unauthenticated()
    {
        var act = async () => await PlainClient().CompleteAsync(
            Request(), cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.Unauthenticated);
    }

    // T-S-03: 🔴 陰性対照。**利用者のトークン（管理者であっても）を転送しても通らない** ——
    // s2s の面は platform-service ロールだけを通す。これが緩むと利用者トークンの転送
    // （confused deputy）が成立する。IADR-0379 決定 4 を機械で守る点である。
    [Fact]
    public async Task Complete_with_forwarded_admin_user_token_is_permission_denied()
    {
        var userToken = GrpcKestrelFactory.IssueToken("admin-user", [PlatformAuthPolicies.AdminRole]);

        var act = async () => await PlainClient().CompleteAsync(
            Request(), headers: Bearer(userToken),
            cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.PermissionDenied);
    }

    // T-S-04: REST と gRPC は同じ判定器（CompletionUseCase）を呼ぶ —— 同じ入力に同じ答えを返す。
    // （REST が同じプロセスの HTTP/1.1 ポートで応えることが、ポート維持の証明でもある。）
    [Fact]
    public async Task Rest_and_grpc_complete_the_same_input_identically()
    {
        var grpc = await PlainClient().CompleteAsync(
            Request(), headers: Bearer(ServiceToken()),
            cancellationToken: TestContext.Current.CancellationToken);

        using var http = new HttpClient { BaseAddress = new Uri(_factory.HttpAddress) };
        var restResp = await http.PostAsJsonAsync("/complete",
            new CompletionApiRequest("本文", MaxTokens: 100, Model: null,
                Confidentiality: "public", Purpose: "default"),
            TestContext.Current.CancellationToken);
        restResp.EnsureSuccessStatusCode();
        var rest = (await restResp.Content.ReadFromJsonAsync<CompletionApiResponse>(
            TestContext.Current.CancellationToken))!;

        grpc.Sent.Should().Be(rest.Sent);
        grpc.Text.Should().Be(rest.Text);
        grpc.Model.Should().Be(rest.Model);
        grpc.InputTokens.Should().Be(rest.InputTokens);
        grpc.OutputTokens.Should().Be(rest.OutputTokens);
        grpc.StopReason.Should().Be(rest.StopReason);
        grpc.RoutingReason.Should().Be(rest.RoutingReason);
    }

    // T-S-06: 🔴 proto3 に null は無い（IADR-0398 決定 4）。`max_tokens=0` は「0 トークン」ではなく
    // 「未指定」であり、REST の DTO 既定（IADR-0101 の 4096）としてプロバイダへ渡らなければならない。
    //
    // **写し漏れは例外にならない** —— プロバイダが 0 を受け取り、本文が空のまま 200 で返る
    // （thinking が既定で有効なモデルでは特にそう見える）。だからプロバイダが受け取った値を
    // 応答本文へ出し、線の上で観測する。
    [Fact]
    public async Task Zero_max_tokens_reaches_the_provider_as_the_rest_default()
    {
        var unspecified = await PlainClient().CompleteAsync(
            new Pb.CompleteRequest { Prompt = "本文", Confidentiality = "public", Purpose = "default" },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        unspecified.Sent.Should().BeTrue();
        unspecified.Text.Should().Contain("max_tokens=4096",
            "proto3 の既定 0 は「未指定」であり REST の既定 4096 へ写される");

        // 陽性対照: 明示した値はそのまま届く（＝「常に 4096 を書く」壊れ方と区別する）。
        var explicitValue = await PlainClient().CompleteAsync(
            Request(maxTokens: 77), headers: Bearer(ServiceToken()),
            cancellationToken: TestContext.Current.CancellationToken);

        explicitValue.Text.Should().Contain("max_tokens=77");
    }

    // T-S-06（続き）: 負数は INVALID_ARGUMENT。0 が「未指定」を担う以上、0 未満は意味を持たない。
    // 黙って 4096 へ倒すと「送った値と違う上限で課金された」ことに呼び出し元が気付けない。
    [Fact]
    public async Task Negative_max_tokens_is_invalid_argument()
    {
        var act = async () => await PlainClient().CompleteAsync(
            Request(maxTokens: -1), headers: Bearer(ServiceToken()),
            cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.InvalidArgument);
    }

    // T-S-14: 🔴 proto3 の string に null は無い。`model` / `confidentiality` / `purpose` の空文字は
    // REST の null と**同じ**に扱われなければならない（決定 4 の「写し不要」を実測で固定する）。
    //
    // 観測点は 2 つ:
    //   - `confidentiality=""` は restricted（安全側）へ倒れる。**逆へ倒れると機密本文が外部へ出る。**
    //   - `model=""` は「未指定」であり、ルータが用途で選んだモデルが渡る（"" がそのまま渡らない）。
    [Fact]
    public async Task Empty_strings_are_treated_as_unspecified()
    {
        var empty = await PlainClient().CompleteAsync(
            new Pb.CompleteRequest { Prompt = "本文" },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        // 陽性対照: 明示した "restricted" と結果が一致すること（＝空文字が restricted と同じ扱い）。
        var restricted = await PlainClient().CompleteAsync(
            new Pb.CompleteRequest { Prompt = "本文", Confidentiality = "restricted", Purpose = "default" },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        empty.Sent.Should().Be(restricted.Sent);
        empty.RoutingReason.Should().Be(restricted.RoutingReason);

        // `model=""` はルータへ「未指定」として届く —— プロバイダが受け取ったモデル名は空ではない。
        if (empty.Sent)
            empty.Text.Should().NotContain("model=(null)").And.NotContain("model=;");
    }

    // 🔴 T-S-11 相当: **ゲートウェイの縮退は RpcException ではなく sent=false の「応答」で返る**
    // （REST の 200 ＋ Sent=false と同値。IADR-0398 決定 5）。
    // エラーにすると呼び出し側は「後段が答えた縮退」と「輸送が壊れた」を区別できなくなり、
    // AiAnalysis は出典すら返せず、Conversion は理由コードを失う。
    //
    // 観測点は**上流の失敗**である。既定構成ではティアB が有効なので越境拒否は起こらない
    // （restricted でも claude-managed へ route される。実測）—— 「拒否されるはず」で書くと
    // 緑にするために表明を弱める誘因になるので、既定構成で確実に踏める縮退を選ぶ。
    [Fact]
    public async Task Upstream_failure_is_a_response_not_an_error()
    {
        var resp = await PlainClient().CompleteAsync(
            Request(ScriptedLlmProvider.UpstreamFailureMarker + " 本文"),
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        resp.Sent.Should().BeFalse();
        resp.Text.Should().NotBeEmpty("縮退の理由が本文に載る");
        resp.RoutingReason.Should().NotBeEmpty();

        // REST も同じ形で縮退する（判定器が 1 つであることの証明）。
        using var http = new HttpClient { BaseAddress = new Uri(_factory.HttpAddress) };
        var restResp = await http.PostAsJsonAsync("/complete",
            new CompletionApiRequest(ScriptedLlmProvider.UpstreamFailureMarker + " 本文",
                MaxTokens: 100, Model: null, Confidentiality: "public", Purpose: "default"),
            TestContext.Current.CancellationToken);
        restResp.EnsureSuccessStatusCode();
        var rest = (await restResp.Content.ReadFromJsonAsync<CompletionApiResponse>(
            TestContext.Current.CancellationToken))!;

        rest.Sent.Should().BeFalse();
        resp.Text.Should().Be(rest.Text);
        resp.RoutingReason.Should().Be(rest.RoutingReason);
    }

    // T-S-10: 構造の門。gRPC サービス型が ServiceCaller ポリシーを宣言していること
    // （属性が外れると T-S-02 / T-S-03 が落ちるが、どの層で外れたかを名指しするためにここでも固定する）。
    [Fact]
    public void Grpc_service_declares_service_caller_policy()
    {
        var attr = typeof(LlmCompletionGrpcService)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>().SingleOrDefault();

        attr.Should().NotBeNull();
        attr!.Policy.Should().Be(PlatformAuthPolicies.ServiceCaller);
    }
}
