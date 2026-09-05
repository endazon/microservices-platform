using System.Net;
using System.Text;
using AwesomeAssertions;
using GraphService.Domain;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Llm;
using GraphService.Infrastructure.ExternalServices;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace GraphService.Tests.Infrastructure.ExternalServices;

// T-P1-05 —— FR-18, FR-11, ADR-0010, ADR-0025, ADR-0029, ADR-0034 決定 5, ADR-0075,
// IADR-0104, IADR-0266 決定 6, IADR-0379, IADR-0398 (#1255):
// 提案生成の gRPC 実装が、REST 実装と**同じ本文に同じ提案**を返し、**同じ枝で `[]` へ降りる**
// ことを固定する。
//
// 🔴 縮退の向きは埋め込みとは**逆**である（IADR-0398 決定 5）。埋め込みは輸送の失敗を例外のまま
// 上げるが、提案は `[]` へ落とす —— REST 実装が非 2xx・HttpRequestException を `[]` にしており、
// **提案が 0 件になるだけで越境も誤提案も起きない**からである。ここを例外にすると、
// ゲートウェイの不調が利用者の要求そのものを落とす（挙動が変わる）。
[Trait("TestKind", "Unit")]
public class LlmGatewayGrpcSuggestionClientTests
{
    // モデルが返す提案の JSON（REST 実装のテストと同じ形）。
    private const string ProposalJson =
        """[{"kind":"tag","tagValue":"設計","rationale":"本文に設計の記述がある"}]""";

    // 封（SuggestionPrompt）の唯一の構築経路は Seal である。**本文の組み立ては封の中にある**
    // （封を通っていない文字列を送る経路を作らない。IADR-0266 決定 1）。
    private static SuggestionPrompt Prompt()
    {
        var scope = new AccessScopeResponse("user-1",
            [new AttributeFilter("confidentiality", ["public"])], true);
        var node = GraphDocument.Create(
            Guid.NewGuid(), "タイトル",
            new Dictionary<string, string> { ["confidentiality"] = "public" },
            null, DateTimeOffset.UtcNow);
        var origin = AuthorizedNode.Authorize(node, scope)!;
        return SuggestionPrompt.Seal(origin, [], ["related"], ["設計"], scope)!;
    }

    // 🔴 **proto は共通写像（LlmGrpcMapping.ToProto）で組む。手で組み立てない。**
    // 手で組むと、写像そのものの欠陥（例: `Sent` の写し漏れ。IADR-0398 決定 4）を本試験が
    // 検出できなくなる —— 変異検査でそれを実測したので、DTO から写像を通す形へ改めた。
    private static Pb.CompleteResponse Gateway(
        bool sent = true, string text = ProposalJson, string stopReason = "end_turn") =>
        LlmGrpcMapping.ToProto(RestGateway(sent, text, stopReason));

    private static CompletionApiResponse RestGateway(
        bool sent = true, string text = ProposalJson, string stopReason = "end_turn") => new(
            Text: text, Model: "claude-opus-5", InputTokens: 11, OutputTokens: 22,
            Sent: sent, Endpoint: "claude-managed", RoutingReason: "ok", StopReason: stopReason);

    private static LlmGatewayGrpcSuggestionClient Client(Pb.CompleteResponse response) =>
        new(new FakeClient(response), NullLogger<LlmGatewayGrpcSuggestionClient>.Instance);

    // 陽性: 提案を読み取る。**陽性が無いと、以下の陰性はすべて「常に空を返す」実装でも緑になる。**
    [Fact]
    public async Task ProposeAsync_ゲートウェイ応答の提案を読み取る()
    {
        var proposals = await Client(Gateway()).ProposeAsync(
            Prompt(), TestContext.Current.CancellationToken);

        proposals.Should().ContainSingle();
        proposals[0].Kind.Should().Be("tag");
        proposals[0].TagValue.Should().Be("設計");
    }

    // T-P1-05 の本丸: **同じ JSON 本文で REST 実装と gRPC 実装の提案が一致する。**
    // 読み取りを共通 static（SuggestionProposalParser）へ寄せたことの実効性でもある。
    [Fact]
    public async Task Rest_と_grpc_は同じ本文に同じ提案を返す()
    {
        var prompt = Prompt();
        var grpc = await Client(Gateway()).ProposeAsync(prompt, TestContext.Current.CancellationToken);

        // REST 側も**同じ DTO**から組む（本文を 2 つ書くと片方だけ直して「一致した」ことにできる）。
        var restJson = System.Text.Json.JsonSerializer.Serialize(RestGateway(),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var rest = await new LlmGatewaySuggestionClient(
                new HttpClient(new StubHandler(restJson)) { BaseAddress = new Uri("http://llm-gateway") },
                NullLogger<LlmGatewaySuggestionClient>.Instance)
            .ProposeAsync(prompt, TestContext.Current.CancellationToken);

        grpc.Should().BeEquivalentTo(rest);
    }

    // 🔴 IADR-0266 決定 6: **縮退した応答を根拠に使わない。**
    // `sent=false`（越境拒否）と `stop_reason=refusal`（モデルが拒否）はどちらも提案 0 件である。
    //
    // 🔴 `sent` は proto3 の既定（false）と DTO の既定（true）で向きが逆であり、
    // ゲートウェイが明示的に書いている（IADR-0398 決定 4）。写し漏れるとこの経路が**常に**
    // 成立して提案が消える —— 上の陽性がその写しの対である。
    [Theory]
    [InlineData(false, "end_turn")]
    [InlineData(true, "refusal")]
    public async Task ProposeAsync_縮退した応答では提案を作らない(bool sent, string stopReason)
    {
        var proposals = await Client(Gateway(sent: sent, stopReason: stopReason)).ProposeAsync(
            Prompt(), TestContext.Current.CancellationToken);

        proposals.Should().BeEmpty();
    }

    // 🔴 輸送の失敗は `[]` へ落とす（REST 実装と同じ枝）。**例外にしない。**
    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.PermissionDenied)]
    public async Task ProposeAsync_RpcException_は空へ落とす(StatusCode status)
    {
        var client = new LlmGatewayGrpcSuggestionClient(
            new ThrowingClient(new RpcException(new Status(status, "denied"))),
            NullLogger<LlmGatewayGrpcSuggestionClient>.Instance);

        var proposals = await client.ProposeAsync(Prompt(), TestContext.Current.CancellationToken);

        proposals.Should().BeEmpty();
    }

    // 🔴 s2s トークンの取得失敗（ClientCredentialsServiceTokenProvider の InvalidOperationException）も
    // 同じ枝である —— 構成不備で提案が付かないことはあっても、利用者の要求は落とさない。
    [Fact]
    public async Task ProposeAsync_s2s_トークン取得失敗も空へ落とす()
    {
        var client = new LlmGatewayGrpcSuggestionClient(
            new ThrowingClient(new InvalidOperationException("ServiceToken:ClientId が未設定です。")),
            NullLogger<LlmGatewayGrpcSuggestionClient>.Instance);

        var proposals = await client.ProposeAsync(Prompt(), TestContext.Current.CancellationToken);

        proposals.Should().BeEmpty();
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

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }
}
