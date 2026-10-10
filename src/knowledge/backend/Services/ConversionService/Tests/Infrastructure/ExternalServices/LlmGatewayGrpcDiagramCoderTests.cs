using AwesomeAssertions;
using ConversionService.Domain;
using ConversionService.Domain.Ports;
using ConversionService.Infrastructure.Configuration;
using ConversionService.Infrastructure.ExternalServices;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Llm;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace ConversionService.Tests.Infrastructure.ExternalServices;

// T-P1-08 —— FR-12, FR-11, ADR-0010, ADR-0012, ADR-0025, ADR-0029, ADR-0075,
// IADR-0104, IADR-0379, IADR-0400 (#1255):
// 図のコード化の gRPC 実装が、**4 経路すべて**（success / egress-denied / llm-refused / not-codeable）で
// 経路ごとに違う帰結を返すことを固定する。
// ［2026-10-10 / #1255］[[IADR-0533]]: REST 実装を撤去したので、REST との同値の Theory は撤去した（絶対値の表明だけが残る）。
// REST 実装の単体試験の応答の読み取りと用途名の表明は `LlmGatewayGrpcDiagramCoderInterpretationTests` へ移した。
//
// 🔴 理由コード（`Reason`）まで一致させる。運用の集計は「何件がどの理由で画像保持になったか」で
// 読むため、理由が輸送で割れると、gRPC へ切り替えた瞬間に集計が別物になる（例外は 1 つも出ない）。
//
// 🔴 輸送の失敗は例外にせず `Retain("llm-call-failed")` へ落とす（IADR-0400 決定 5）——
// 変換パイプラインを止めないための deny-by-default である（旧 REST 実装の非 2xx・接続失敗と同じ理由文字列）。
[Trait("TestKind", "Unit")]
public class LlmGatewayGrpcDiagramCoderTests
{
    private static ExtractedFigure Figure() => new("fig-1", "image/png", [1, 2, 3]);

    private static LlmGatewayGrpcDiagramCoder Coder(CompletionApiResponse response) =>
        new(new FakeClient(LlmGrpcMapping.ToProto(response)),
            NullLogger<LlmGatewayGrpcDiagramCoder>.Instance);

    // 4 経路のゲートウェイ応答。
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

    // 🔴 T-P1-08 の本丸: 4 経路が**それぞれ違う帰結**であることを絶対値で固定する（理由コードまで）。
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

    // FR-12 テスト仕様 T-45 (#1621), UC-06 例外フロー: gRPC の期限切れ・取り消しは `RpcException(DeadlineExceeded / Cancelled)`
    // で表れる（チャネルは `ThrowOperationCanceledOnCancellation` を立てていない）。
    // **呼び出し元の ct が立っていなければ**、時間切れも輸送の失敗として画像保持へ畳む（REST の FR-12 T-44 と同じ境界）。
    [Theory]
    [InlineData(StatusCode.DeadlineExceeded)]
    [InlineData(StatusCode.Cancelled)]
    public async Task 呼び出し元に由来しない期限切れと取り消しは画像保持へ縮退する(StatusCode status)
    {
        var coder = new LlmGatewayGrpcDiagramCoder(
            new ThrowingClient(new RpcException(new Status(status, "gateway timed out"))),
            NullLogger<LlmGatewayGrpcDiagramCoder>.Instance);

        var result = await coder.CodeAsync(Figure(), "internal", TestContext.Current.CancellationToken);

        result.Coded.Should().BeFalse();
        result.Reason.Should().Be("llm-call-failed");
    }

    // FR-12 T-45 の対照 (#1621): **呼び出し元（受け口の ct ＝停止要求と Wolverine の実行期限の連結）の取り消しは畳まずに外へ出す。**
    // 呼び出し元の ct が生成クライアントへ渡っていること（`CallOptions.CancellationToken`）も併せて見る ——
    // 渡っていなければ、`!ct.IsCancellationRequested` の絞りは実際の取り消しと結び付かない。
    [Fact]
    public async Task 呼び出し元の取り消しは畳まずに外へ出す()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var client = new CancellingClient(cts);
        var coder = new LlmGatewayGrpcDiagramCoder(client, NullLogger<LlmGatewayGrpcDiagramCoder>.Instance);

        var act = () => coder.CodeAsync(Figure(), "internal", cts.Token);

        var thrown = await act.Should().ThrowAsync<RpcException>();
        thrown.Which.StatusCode.Should().Be(StatusCode.Cancelled);
        client.ReceivedToken.Should().Be(cts.Token);
    }

    // FR-12 T-45 (#1621), IADR-0008（2026-09-27 追記）: **呼び出しごとに期限を付ける**（値は REST の `HttpClient.Timeout` と
    // 同じ `DiagramCodingLimits.CallTimeout`）。期限が無いと、応答しないゲートウェイは受け口の ct（Wolverine の実行期限を含む）が
    // 先に立つ形でしか終わらず、上の縮退の枝に届かない。
    [Fact]
    public async Task 呼び出しごとに構成の期限を付ける()
    {
        var time = new ManualTimeProvider();
        var client = new CapturingClient();
        var coder = new LlmGatewayGrpcDiagramCoder(client, NullLogger<LlmGatewayGrpcDiagramCoder>.Instance,
            DiagramCodingLimits.Default with { CallTimeout = TimeSpan.FromSeconds(17) }, time);

        await coder.CodeAsync(Figure(), "internal", TestContext.Current.CancellationToken);

        client.Deadline.Should().Be((time.GetUtcNow() + TimeSpan.FromSeconds(17)).UtcDateTime);
    }

    // 呼び出しの選択肢（期限）を記録し、コード化できない応答を返す。
    private sealed class CapturingClient : Pb.LlmCompletion.LlmCompletionClient
    {
        public DateTime? Deadline { get; private set; }

        public override AsyncUnaryCall<Pb.CompleteResponse> CompleteAsync(
            Pb.CompleteRequest request, CallOptions options)
        {
            Deadline = options.Deadline;
            var response = LlmGrpcMapping.ToProto(new CompletionApiResponse(
                Text: "不可", Model: "m", InputTokens: 1, OutputTokens: 1));
            return new(Task.FromResult(response), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
        }
    }

    // 呼び出しの途中で呼び出し元の ct を取り消し、実チャネルと同じ `RpcException(Cancelled)` で終わる。
    private sealed class CancellingClient(CancellationTokenSource caller) : Pb.LlmCompletion.LlmCompletionClient
    {
        public CancellationToken ReceivedToken { get; private set; }

        public override AsyncUnaryCall<Pb.CompleteResponse> CompleteAsync(
            Pb.CompleteRequest request, CallOptions options)
        {
            ReceivedToken = options.CancellationToken;
            caller.Cancel();
            return new(Task.FromException<Pb.CompleteResponse>(
                    new RpcException(new Status(StatusCode.Cancelled, "Call canceled by the client."))),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
        }
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
}
