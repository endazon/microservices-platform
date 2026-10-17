using AwesomeAssertions;
using GraphService.Domain.Clustering;
using GraphService.Domain.Ports;
using GraphService.Infrastructure.ExternalServices;
using Grpc.Core;
using Knowledge.Contracts.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Llm;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace GraphService.Tests.Infrastructure.ExternalServices;

// FR-11, FR-17, FR-18, NFR-16, ADR-0010, ADR-0029, ADR-0035 決定 3, ADR-0075, ADR-0089 決定 1,
// [[IADR-0266]] 決定 6, [[IADR-0400]] 決定 4・5, [[IADR-0430]] 決定 1・4, [[IADR-0533]] フォローアップ 2 (#1395, #1255):
// クラスタ要約の gRPC 実装の**写像と縮退**を、生成クライアントの偽物の上で固定する。
// ［2026-10-17 / #1255］REST 実装（`LlmGatewayClusterSummaryClient`）とその試験は撤去した。REST の試験が持っていた性質
// （用途名・機密区分・モデル未指定・縮退した応答・不達）はすべてここへ写した（作業仕様書 §試験の写し方）。
[Trait("TestKind", "Unit")]
public class LlmGatewayGrpcClusterSummaryClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClusterSummaryPrompt Prompt(string tier = ConfidentialityLevels.Internal)
        => ClusterSummaryPrompt.Seal(Guid.NewGuid(), tier, [
            new ClusterMemberDocument(Guid.NewGuid(), "設計メモ",
                new Dictionary<string, string> { [ConfidentialityLevels.AttributeKey] = tier }),
        ])!;

    // 🔴 **proto は共通写像（LlmGrpcMapping.ToProto）で組む。手で組み立てない。**
    // 手で組むと、写像そのものの欠陥（`Sent` の写し漏れ。IADR-0400 決定 4）を本試験が検出できなくなる
    // （兄弟の `LlmGatewayGrpcSuggestionClientTests` が変異検査で実測した作法）。
    private static Pb.CompleteResponse Gateway(
        string text = "この集まりは設計メモである。", bool sent = true, string? stopReason = "end_turn") =>
        LlmGrpcMapping.ToProto(new CompletionApiResponse(
            Text: text, Model: "claude-opus-5-5", InputTokens: 10, OutputTokens: 20,
            Sent: sent, Endpoint: "claude-managed", RoutingReason: "ok", StopReason: stopReason));

    private static LlmGatewayGrpcClusterSummaryClient Client(Pb.LlmCompletion.LlmCompletionClient client) =>
        new(client, NullLogger<LlmGatewayGrpcClusterSummaryClient>.Instance);

    // 🔴 (T-8) 陽性: **用途名は `graph-cluster-summary` である。** AI 提案（`graph-suggestion`）と
    // 同じ値にすると、用途別の費用集計で 2 経路を区別できなくなる。機密区分も封の値をそのまま渡す。
    // **陽性が無いと、以下の陰性はすべて「常に null を返す」実装でも緑になる。**
    [Fact]
    public async Task 用途名と機密区分を添えてCompleteを呼び_応答本文を要約として返す()
    {
        var fake = new FakeClient(Gateway("  この集まりは設計メモである。 "));
        var prompt = Prompt(ConfidentialityLevels.Confidential);

        var summary = await Client(fake).SummarizeAsync(prompt, Ct);

        summary.Should().Be("この集まりは設計メモである。");
        fake.LastRequest.Should().NotBeNull();
        fake.LastRequest!.Purpose.Should().Be("graph-cluster-summary");
        fake.LastRequest.Confidentiality.Should().Be(ConfidentialityLevels.Confidential);
        fake.LastRequest.Prompt.Should().Be(prompt.Render(), "送信本文は封が組み立てる");
        // 🔴 モデルは指定しない（宛先の決定はゲートウェイの経路決定が持つ。proto3 の空文字＝未指定）。
        fake.LastRequest.Model.Should().BeEmpty("呼び出し側がモデルを固定すると越境判定が選んだ宛先と食い違い得る");
    }

    // 🔴 (T-9): [[IADR-0266]] 決定 6 —— **縮退した応答を根拠に使わない。**
    // `sent=false`（越境させていない）・`refusal`（モデルが拒否した）・空文字はいずれも
    // 「採れなかった」であり、**要約として書いてはならない**。
    [Theory]
    [InlineData(false, "end_turn", "本文", "sent=false は機密区分による送信拒否である")]
    [InlineData(true, "refusal", "本文", "refusal は送信は成立したがモデルが拒否した")]
    [InlineData(true, "end_turn", "", "空の要約を書くと中身の無い行が残る")]
    [InlineData(true, "end_turn", "   ", "空白だけの要約も同じ")]
    public async Task 縮退した応答は要約として採らない(bool sent, string stopReason, string text, string because)
    {
        var summary = await Client(new FakeClient(Gateway(text, sent, stopReason))).SummarizeAsync(Prompt(), Ct);

        summary.Should().BeNull(because);
    }

    // (T-9): 輸送の失敗は例外にせず null へ落とす（1 区分の失敗はそのクラスタを未要約に残すだけである）。
    // `Unavailable` は不達と、宛先未構成（`UnconfiguredGrpcDestination`）の両方を表す。
    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.DeadlineExceeded)]
    public async Task RpcExceptionは例外にせずnullへ落とす(StatusCode status)
    {
        var client = Client(new ThrowingClient(new RpcException(new Status(status, "denied"))));

        var summary = await client.SummarizeAsync(Prompt(), Ct);

        summary.Should().BeNull();
    }

    // 🔴 s2s トークンの取得失敗（ClientCredentialsServiceTokenProvider の InvalidOperationException）も同じ枝である。
    [Fact]
    public async Task s2sトークン取得失敗もnullへ落とす()
    {
        var client = Client(new ThrowingClient(new InvalidOperationException("ServiceToken:ClientId が未設定です。")));

        var summary = await client.SummarizeAsync(Prompt(), Ct);

        summary.Should().BeNull();
    }

    // 🔴 呼び出し元の取り消しは null へ畳まず伝播する —— 畳むと、ホストの停止が「要約が採れなかった」に化け、
    // バッチが残りのクラスタへ進み続ける。
    [Fact]
    public async Task 呼び出し元の取り消しは伝播する()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var client = Client(new ThrowingClient(new RpcException(new Status(StatusCode.Cancelled, "cancelled"))));

        var act = async () => await client.SummarizeAsync(Prompt(), cts.Token);

        await act.Should().ThrowAsync<RpcException>();
    }

    private sealed class FakeClient(Pb.CompleteResponse response) : Pb.LlmCompletion.LlmCompletionClient
    {
        public Pb.CompleteRequest? LastRequest { get; private set; }

        public override AsyncUnaryCall<Pb.CompleteResponse> CompleteAsync(
            Pb.CompleteRequest request, CallOptions options)
        {
            LastRequest = request;
            return new(Task.FromResult(response), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
        }
    }

    private sealed class ThrowingClient(Exception exception) : Pb.LlmCompletion.LlmCompletionClient
    {
        public override AsyncUnaryCall<Pb.CompleteResponse> CompleteAsync(
            Pb.CompleteRequest request, CallOptions options) =>
            new(Task.FromException<Pb.CompleteResponse>(exception), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
    }
}

// 🔴 (#1255) 回帰対照: **本番 DI が組むクラスタ要約の LLM 境界は gRPC 実装である。**
// `TestWebApplicationFactory` は `IClusterSummaryLlmClient` を差し替えないので、Program.cs の登録をそのまま解決する。
// 登録を REST 実装（`AddHttpClient<…>`）へ戻す変異、または別実装へ差し替える変異で赤になる。
[Trait("TestKind", "Integration")]
public class ClusterSummaryLlmWiringTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public void 既定構成のDIはクラスタ要約にgRPC実装を解決する()
    {
        using var scope = factory.Services.CreateScope();

        var llm = scope.ServiceProvider.GetRequiredService<IClusterSummaryLlmClient>();

        llm.Should().BeOfType<LlmGatewayGrpcClusterSummaryClient>(
            "east-west の輸送は gRPC だけである（REST の /complete へ戻さない）");
    }
}
