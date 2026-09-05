using AwesomeAssertions;
using ConversionService.Domain;
using ConversionService.Domain.Ports;
using ConversionService.Infrastructure.ExternalServices;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Llm;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace ConversionService.Tests.Infrastructure.ExternalServices;

// T-P1-08 —— FR-12, FR-11, ADR-0010, ADR-0012, ADR-0025, ADR-0029, ADR-0075,
// IADR-0104, IADR-0379, IADR-0398 (#1255):
// 図のコード化の gRPC 実装が、REST 実装と**同じゲートウェイ応答に同じ帰結**を返すことを
// **4 経路すべて**（success / egress-denied / llm-refused / not-codeable）で固定する。
//
// 🔴 理由コード（`Reason`）まで一致させる。運用の集計は「何件がどの理由で画像保持になったか」で
// 読むため、理由が輸送で割れると、gRPC へ切り替えた瞬間に集計が別物になる（例外は 1 つも出ない）。
//
// 🔴 輸送の失敗は例外にせず `Retain("llm-call-failed")` へ落とす（IADR-0398 決定 5）——
// REST 実装が `EnsureSuccessStatusCode` の例外と接続失敗を同じ理由で画像保持にしているのと同じ枝。
// 変換パイプラインを止めないための deny-by-default である。
[Trait("TestKind", "Unit")]
public class LlmGatewayGrpcDiagramCoderTests
{
    private static ExtractedFigure Figure() => new("fig-1", "image/png", [1, 2, 3]);

    private static LlmGatewayGrpcDiagramCoder Coder(CompletionApiResponse response) =>
        new(new FakeClient(LlmGrpcMapping.ToProto(response)),
            NullLogger<LlmGatewayGrpcDiagramCoder>.Instance);

    private static LlmGatewayDiagramCoder RestCoder(CompletionApiResponse response) =>
        new(new HttpClient(new JsonStubHandler(response)) { BaseAddress = new Uri("http://llm-gateway:5007") },
            NullLogger<LlmGatewayDiagramCoder>.Instance);

    // 4 経路のゲートウェイ応答。**REST 実装の既存テストと同じ値**である。
    public static TheoryData<string, CompletionApiResponse> Paths() => new()
    {
        {
            "success",
            new CompletionApiResponse(
                Text: "```mermaid\ngraph TD; A-->B\n```", Model: "m", InputTokens: 1, OutputTokens: 1)
        },
        {
            "egress-denied",
            new CompletionApiResponse(
                Text: "送信できません", Model: "", InputTokens: 0, OutputTokens: 0,
                Sent: false, Endpoint: null, RoutingReason: "restricted-blocked")
        },
        {
            "llm-refused",
            new CompletionApiResponse(
                Text: "", Model: "m", InputTokens: 1, OutputTokens: 0,
                Sent: true, Endpoint: "claude-managed", RoutingReason: "ok",
                StopReason: CompletionStopReasons.Refusal)
        },
        {
            "not-codeable",
            new CompletionApiResponse(
                Text: "不可", Model: "m", InputTokens: 1, OutputTokens: 1)
        },
    };

    // 🔴 T-P1-08 の本丸: **4 経路すべてで REST と gRPC の帰結（Coded / Language / Code / Reason）が一致する。**
    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Rest_と_grpc_は同じ応答に同じ帰結を返す(string path, CompletionApiResponse gateway)
    {
        var grpc = await Coder(gateway).CodeAsync(
            Figure(), "internal", TestContext.Current.CancellationToken);
        var rest = await RestCoder(gateway).CodeAsync(
            Figure(), "internal", TestContext.Current.CancellationToken);

        grpc.Coded.Should().Be(rest.Coded, "経路 {0}", path);
        grpc.Language.Should().Be(rest.Language, "経路 {0}", path);
        grpc.Code.Should().Be(rest.Code, "経路 {0}", path);
        grpc.Reason.Should().Be(rest.Reason, "経路 {0}", path);
    }

    // 陽性対照つきの絶対値。上の同値だけだと「両方が同じように壊れている」場合に緑になる ——
    // 4 経路が**それぞれ違う帰結**であることをここで固定する。
    [Theory]
    [InlineData("success", true, null)]
    [InlineData("egress-denied", false, "egress-denied")]
    [InlineData("llm-refused", false, "llm-refused")]
    [InlineData("not-codeable", false, "not-codeable")]
    public async Task Grpc_の帰結は経路ごとに異なる(string path, bool coded, string? reasonPrefix)
    {
        var gateway = Paths().Cast<TheoryDataRow<string, CompletionApiResponse>>()
            .Select(r => r.Data).First(d => d.Item1 == path).Item2;

        var result = await Coder(gateway).CodeAsync(
            Figure(), "internal", TestContext.Current.CancellationToken);

        result.Coded.Should().Be(coded);
        if (reasonPrefix is not null)
            result.Reason.Should().StartWith(reasonPrefix);
    }

    // 🔴 輸送の失敗は `Retain("llm-call-failed")` —— **REST と同じ理由文字列**である。
    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.PermissionDenied)]
    public async Task RpcException_は画像保持へ縮退する(StatusCode status)
    {
        var coder = new LlmGatewayGrpcDiagramCoder(
            new ThrowingClient(new RpcException(new Status(status, "denied"))),
            NullLogger<LlmGatewayGrpcDiagramCoder>.Instance);

        var result = await coder.CodeAsync(Figure(), "internal", TestContext.Current.CancellationToken);

        result.Coded.Should().BeFalse();
        result.Reason.Should().Be("llm-call-failed");
    }

    // 🔴 s2s トークンの取得失敗も同じ枝である（構成不備で変換が止まらない）。
    [Fact]
    public async Task s2s_トークン取得失敗も画像保持へ縮退する()
    {
        var coder = new LlmGatewayGrpcDiagramCoder(
            new ThrowingClient(new InvalidOperationException("ServiceToken:ClientId が未設定です。")),
            NullLogger<LlmGatewayGrpcDiagramCoder>.Instance);

        var result = await coder.CodeAsync(Figure(), "internal", TestContext.Current.CancellationToken);

        result.Coded.Should().BeFalse();
        result.Reason.Should().Be("llm-call-failed");
    }

    private sealed class FakeClient(Pb.CompleteResponse response) : Pb.LlmCompletion.LlmCompletionClient
    {
        public override AsyncUnaryCall<Pb.CompleteResponse> CompleteAsync(
            Pb.CompleteRequest request, CallOptions options) =>
            new(Task.FromResult(response), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
    }

    private sealed class ThrowingClient(Exception exception) : Pb.LlmCompletion.LlmCompletionClient
    {
        public override AsyncUnaryCall<Pb.CompleteResponse> CompleteAsync(
            Pb.CompleteRequest request, CallOptions options) =>
            new(Task.FromException<Pb.CompleteResponse>(exception), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
    }

    private sealed class JsonStubHandler(CompletionApiResponse response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = System.Net.Http.Json.JsonContent.Create(response),
            });
    }
}
