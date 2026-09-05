using System.Net;
using System.Text;
using AwesomeAssertions;
using Grpc.Core;
using IngestionService.Infrastructure.ExternalServices;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace IngestionService.Tests.Infrastructure.ExternalServices;

// T-P1-07 —— FR-02, FR-05, ADR-0013, ADR-0016, ADR-0029, ADR-0075, IADR-0256, IADR-0379, IADR-0397 (#1255):
// 取り込み埋め込みの gRPC 実装が、REST 実装（LlmGatewayEmbeddingServiceTests）と**同じ
// `EmbeddingResult(Vector, Collection, Embedded, Retryable)`** を返し、**輸送の失敗では例外を上げる**
// ことを固定する。
//
// 🔴 既存の REST 側の 3 ケース（正常・fail-closed・本文欠落）と対で読むこと。gRPC には「本文欠落」が
// 無い（不達は RpcException になる）ので、その 1 ケースは**例外の側**へ移っている。
[Trait("TestKind", "Unit")]
public class LlmGatewayGrpcEmbeddingServiceTests
{
    // 稼働クラスタから採取した REST 応答（既存テストの GatewayResponse）と同じ内容の proto 応答。
    private static Pb.EmbedResponse GatewayResponse(
        bool embedded = true, bool retryable = false, params float[] vector)
    {
        var resp = new Pb.EmbedResponse
        {
            Dimensions = vector.Length,
            Model = "deterministic-hash-v1",
            Collection = embedded ? "knowledge_chunks_deterministic_v1" : string.Empty,
            Embedded = embedded,
            Endpoint = "deterministic-local",
            RoutingReason = "機密区分 Public / 用途 Index",
            Retryable = retryable,
        };
        resp.Vector.AddRange(vector);
        return resp;
    }

    // 既存 REST テストの 1 件目と同値。🔴 ここが空になると、索引は「成功したことになって 0 点」になる。
    [Fact]
    public async Task EmbedAsync_ゲートウェイ応答のベクトルを落とさずに運ぶ()
    {
        var result = await new LlmGatewayGrpcEmbeddingService(
                new FakeClient(GatewayResponse(vector: [0.1f, -0.2f, 0.3f])))
            .EmbedAsync("本文", "public", TestContext.Current.CancellationToken);

        result.Vector.Should().HaveCount(3);
        result.Embedded.Should().BeTrue();
        result.Collection.Should().Be("knowledge_chunks_deterministic_v1");
        result.Retryable.Should().BeFalse();
    }

    // 既存 REST テストの 2 件目と同値。送信拒否（機密区分 × ティア）は**再試行しない**。
    [Fact]
    public async Task EmbedAsync_fail_closed_の応答をそのまま伝える()
    {
        var result = await new LlmGatewayGrpcEmbeddingService(
                new FakeClient(GatewayResponse(embedded: false)))
            .EmbedAsync("本文", "confidential", TestContext.Current.CancellationToken);

        result.Embedded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Vector.Should().BeEmpty();
    }

    // 一時障害（上流不調）は Retryable=true を**そのまま**運ぶ（恒久スキップにしない）。
    [Fact]
    public async Task EmbedAsync_一時障害の_Retryable_をそのまま運ぶ()
    {
        var result = await new LlmGatewayGrpcEmbeddingService(
                new FakeClient(GatewayResponse(embedded: false, retryable: true)))
            .EmbedAsync("本文", "public", TestContext.Current.CancellationToken);

        result.Embedded.Should().BeFalse();
        result.Retryable.Should().BeTrue();
    }

    // 同値: REST 実装と gRPC 実装が**同じゲートウェイ応答**に対して同じ EmbeddingResult を返す。
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Rest_と_grpc_は同じ応答に同じ戻りを返す(bool embedded, bool retryable)
    {
        float[] vector = embedded ? [0.1f, -0.2f, 0.3f] : [];
        var grpc = await new LlmGatewayGrpcEmbeddingService(
                new FakeClient(GatewayResponse(embedded, retryable, vector)))
            .EmbedAsync("本文", "public", TestContext.Current.CancellationToken);

        var json = $$"""
            {"vector":[{{string.Join(",", vector.Select(v => v.ToString("R")))}}],
             "dimensions":{{vector.Length}},"model":"deterministic-hash-v1",
             "collection":"{{(embedded ? "knowledge_chunks_deterministic_v1" : "")}}",
             "embedded":{{(embedded ? "true" : "false")}},"endpoint":"deterministic-local",
             "routingReason":"機密区分 Public / 用途 Index","retryable":{{(retryable ? "true" : "false")}}}
            """;
        var rest = await new LlmGatewayEmbeddingService(
                new HttpClient(new StubHandler(json)) { BaseAddress = new Uri("http://llm-gateway") })
            .EmbedAsync("本文", "public", TestContext.Current.CancellationToken);

        grpc.Should().BeEquivalentTo(rest);
    }

    // 🔴 T-P1-07 の本丸: 輸送の不達（UNAVAILABLE）は**例外のまま上がる**（現行 REST の
    // EnsureSuccessStatusCode と同じ判断）。ここで Retryable=true へ倒すと、ゲートウェイの故障と
    // 機密区分による送信拒否が同じ形になり区別できなくなる（IADR-0256 決定 3）。
    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.PermissionDenied)]
    public async Task EmbedAsync_輸送の失敗は例外のまま上げる(StatusCode status)
    {
        var service = new LlmGatewayGrpcEmbeddingService(
            new ThrowingClient(new RpcException(new Status(status, "transport"))));

        var act = async () => await service.EmbedAsync("本文", "public", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(status);
    }

    // s2s トークンの取得失敗（構成不備・IdP 不達）も握り潰さない。
    [Fact]
    public async Task EmbedAsync_s2s_トークン取得失敗も例外のまま上げる()
    {
        var service = new LlmGatewayGrpcEmbeddingService(
            new ThrowingClient(new InvalidOperationException("ServiceToken:ClientId が未設定です。")));

        var act = async () => await service.EmbedAsync("本文", "public", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed class FakeClient(Pb.EmbedResponse response) : Pb.LlmEmbedding.LlmEmbeddingClient
    {
        public override AsyncUnaryCall<Pb.EmbedResponse> EmbedAsync(
            Pb.EmbedRequest request, CallOptions options) => new(
                Task.FromResult(response), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
    }

    private sealed class ThrowingClient(Exception exception) : Pb.LlmEmbedding.LlmEmbeddingClient
    {
        public override AsyncUnaryCall<Pb.EmbedResponse> EmbedAsync(
            Pb.EmbedRequest request, CallOptions options) => new(
                Task.FromException<Pb.EmbedResponse>(exception), Task.FromResult(new Metadata()),
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
