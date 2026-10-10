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

// FR-12, ADR-0012/0010: 図コード化クライアント（LLM ゲートウェイの `Complete`）の応答の読み取りの単体テスト。
// ［2026-10-10 / #1255］[[IADR-0533]]: 旧 REST 実装（`LlmGatewayDiagramCoder`）の単体試験を gRPC 実装へ移した（本ファイルへ改名）。
// 応答の読み取り（言語とコードの抽出・送信拒否・拒否・コード化不能）と送る用途名の表明はそのまま残し、
// HTTP に固有の失敗の形（接続失敗・`HttpClient.Timeout`・取り消しの `TaskCanceledException`）の表明は撤去した ——
// gRPC の失敗の形（`RpcException`・`Deadline`・`Cancelled`）は `LlmGatewayGrpcDiagramCoderTests` が測る。
[Trait("TestKind", "Unit")]
public class LlmGatewayGrpcDiagramCoderInterpretationTests
{
    private static ExtractedFigure Figure() => new("fig-1", "image/png", [1, 2, 3]);

    private static LlmGatewayGrpcDiagramCoder Coder(CompletionApiResponse response) =>
        new(new ReplayClient(response), NullLogger<LlmGatewayGrpcDiagramCoder>.Instance);

    // Mermaid のフェンス付きコードが返れば、言語とコードを抽出して成功とする。
    [Fact]
    public async Task Codes_diagram_from_fenced_mermaid()
    {
        var coder = Coder(new CompletionApiResponse(
            Text: "```mermaid\ngraph TD; A-->B\n```", Model: "m", InputTokens: 1, OutputTokens: 1));

        var result = await coder.CodeAsync(Figure(), "internal", TestContext.Current.CancellationToken);

        result.Coded.Should().BeTrue();
        result.Language.Should().Be("mermaid");
        result.Code.Should().Be("graph TD; A-->B");
    }

    // 機密区分で送信拒否（Sent=false）なら、画像として保持する（コード化しない）。
    [Fact]
    public async Task Retains_when_egress_denied()
    {
        var coder = Coder(new CompletionApiResponse(
            Text: "送信できません", Model: "", InputTokens: 0, OutputTokens: 0,
            Sent: false, Endpoint: null, RoutingReason: "restricted-blocked"));

        var result = await coder.CodeAsync(Figure(), "restricted", TestContext.Current.CancellationToken);

        result.Coded.Should().BeFalse();
        result.Reason.Should().Contain("egress-denied");
    }

    // T-16, IADR-0104 (#379): モデルが拒否（stopReason="refusal"）した場合も画像として保持するが、
    // 理由は「コード化不能」ではなく拒否として記録する。拒否は sent=true・本文空で返るため、
    // sent とフェンスの有無だけを見ると「図をコード化できなかった」と誤って記録される。
    [Fact]
    public async Task Retains_with_refusal_reason_when_model_refuses()
    {
        var coder = Coder(new CompletionApiResponse(
            Text: "", Model: "claude-haiku-5-5", InputTokens: 1, OutputTokens: 0,
            Sent: true, Endpoint: "claude-managed", RoutingReason: "ok",
            StopReason: CompletionStopReasons.Refusal));

        var result = await coder.CodeAsync(Figure(), "internal", TestContext.Current.CancellationToken);

        result.Coded.Should().BeFalse();          // 画像保持（fail-safe は不変）
        result.Reason.Should().Be("llm-refused");
        result.Reason.Should().NotBe("not-codeable");
    }

    // コード化不能（「不可」等、フェンスなし）なら画像として保持する。
    [Fact]
    public async Task Retains_when_not_codeable()
    {
        var coder = Coder(new CompletionApiResponse(
            Text: "不可", Model: "m", InputTokens: 1, OutputTokens: 1));

        var result = await coder.CodeAsync(Figure(), "internal", TestContext.Current.CancellationToken);

        result.Coded.Should().BeFalse();
        result.Reason.Should().Be("not-codeable");
    }

    // FR-11: 送信 purpose は "diagram-coding"（LlmGateway の PurposeModels 設定キーと一致）であること。
    // このキーが不一致だと用途別モデル（haiku）選択が発火せず既定モデルへ縮退する（Issue #58 の #1）。
    [Fact]
    public async Task Sends_purpose_diagram_coding()
    {
        var capture = new ReplayClient(new CompletionApiResponse(
            Text: "```mermaid\ngraph TD; A-->B\n```", Model: "m", InputTokens: 1, OutputTokens: 1));
        var coder = new LlmGatewayGrpcDiagramCoder(capture, NullLogger<LlmGatewayGrpcDiagramCoder>.Instance);

        await coder.CodeAsync(Figure(), "internal", TestContext.Current.CancellationToken);

        capture.Request.Should().NotBeNull();
        capture.Request!.Purpose.Should().Be("diagram-coding");
        capture.Request.Confidentiality.Should().Be("internal");
    }

    // 送信リクエストを捕捉しつつ、定型応答を返す生成クライアント。
    private sealed class ReplayClient(CompletionApiResponse response) : Pb.LlmCompletion.LlmCompletionClient
    {
        public CompletionApiRequest? Request { get; private set; }

        public override AsyncUnaryCall<Pb.CompleteResponse> CompleteAsync(
            Pb.CompleteRequest request, CallOptions options)
        {
            Request = LlmGrpcMapping.ToDto(request);
            return new(Task.FromResult(LlmGrpcMapping.ToProto(response)), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
        }
    }
}
