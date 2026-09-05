using System.Net;
using System.Text;
using AwesomeAssertions;
using Grpc.Core;
using RetrievalService.Infrastructure.ExternalServices;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace RetrievalService.Tests.Infrastructure.ExternalServices;

// T-P1-06 —— FR-03, FR-05, ADR-0013, ADR-0016, ADR-0029, ADR-0075, IADR-0256, IADR-0379, IADR-0397 (#1255):
// クエリ埋め込みの gRPC 実装が、REST 実装と**同じ戻り**を返し、**輸送の失敗では例外を上げる**ことを固定する。
//
// 🔴 ここが本 PR で最も壊れやすい点である。gRPC の `RpcException` を握り潰して `[]` を返すと、
// HybridSearchService は「意味検索の系統が使えない」と読んで 0 件を返す —— **ゲートウェイの故障が
// 静かに『該当なし』へ化ける**（IADR-0256 決定 3 が名指しで禁じた形）。REST 実装は
// `EnsureSuccessStatusCode` でこれを防いでおり、gRPC 実装も同じ判断でなければならない。
//
// 生成クライアントのメソッドは virtual なので偽物を作れる（参照実装の GrpcResolveScopeTests と同じ手）。
[Trait("TestKind", "Unit")]
public class LlmGatewayGrpcEmbeddingServiceTests
{
    // REST 実装と同じ入力・同じゲートウェイ応答で比較するための応答（EmbeddingEndpointTests と同じ形）。
    private static Pb.EmbedResponse GatewayResponse(bool embedded, params float[] vector)
    {
        var resp = new Pb.EmbedResponse
        {
            Dimensions = vector.Length,
            Model = "voyage-3.5",
            Collection = "knowledge_chunks_voyage_3_5",
            Embedded = embedded,
            Endpoint = "voyage-managed",
            RoutingReason = "機密区分 Public / 用途 Query",
            Retryable = false,
        };
        resp.Vector.AddRange(vector);
        return resp;
    }

    // 陽性: ベクトルを落とさずに運ぶ（#992 で REST 側が踏んだ形の gRPC 版）。
    [Fact]
    public async Task EmbedAsync_ゲートウェイ応答のベクトルを落とさずに運ぶ()
    {
        var service = new LlmGatewayGrpcEmbeddingService(
            new FakeClient(GatewayResponse(embedded: true, 0.1f, -0.2f, 0.3f)));

        var vector = await service.EmbedAsync("問い", TestContext.Current.CancellationToken);

        vector.Should().Equal(0.1f, -0.2f, 0.3f);
    }

    // 陰性（設計上の縮退）: Embedded=false は空ベクトル。REST 実装（#995）と同じ判断である。
    [Fact]
    public async Task EmbedAsync_Embedded_false_は空ベクトルへ降りる()
    {
        var service = new LlmGatewayGrpcEmbeddingService(
            new FakeClient(GatewayResponse(embedded: false)));

        var vector = await service.EmbedAsync("問い", TestContext.Current.CancellationToken);

        vector.Should().BeEmpty();
    }

    // 陽性対照つき同値: REST 実装と gRPC 実装が**同じゲートウェイ応答**に対して同じ戻りを返す。
    // REST 側は同じ内容の JSON をスタブハンドラで返す（項目名のずれもここで捕まる）。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rest_と_grpc_は同じ応答に同じ戻りを返す(bool embedded)
    {
        var vector = embedded ? new[] { 0.5f, 0.25f } : [];
        var grpc = await new LlmGatewayGrpcEmbeddingService(new FakeClient(GatewayResponse(embedded, vector)))
            .EmbedAsync("問い", TestContext.Current.CancellationToken);

        var json = $$"""
            {"vector":[{{string.Join(",", vector.Select(v => v.ToString("R")))}}],
             "dimensions":{{vector.Length}},"model":"voyage-3.5",
             "collection":"knowledge_chunks_voyage_3_5","embedded":{{(embedded ? "true" : "false")}},
             "endpoint":"voyage-managed","routingReason":"機密区分 Public / 用途 Query","retryable":false}
            """;
        var rest = await new LlmGatewayEmbeddingService(
                new HttpClient(new StubHandler(json)) { BaseAddress = new Uri("http://llm-gateway") })
            .EmbedAsync("問い", TestContext.Current.CancellationToken);

        grpc.Should().Equal(rest);
    }

    // 🔴 T-P1-06 の本丸: 輸送の不達（UNAVAILABLE）は**例外のまま上がる**。空ベクトルへ縮退しない。
    [Fact]
    public async Task EmbedAsync_UNAVAILABLE_は例外のまま上げる()
    {
        var service = new LlmGatewayGrpcEmbeddingService(
            new ThrowingClient(new RpcException(new Status(StatusCode.Unavailable, "gateway down"))));

        var act = async () => await service.EmbedAsync("問い", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.Unavailable);
    }

    // 🔴 s2s の面の拒否（UNAUTHENTICATED / PERMISSION_DENIED）も同じく例外のまま上げる ——
    // 資格情報の設定漏れが「検索結果 0 件」として現れると、原因に辿り着けない。
    [Theory]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.PermissionDenied)]
    public async Task EmbedAsync_s2s_の拒否も例外のまま上げる(StatusCode status)
    {
        var service = new LlmGatewayGrpcEmbeddingService(
            new ThrowingClient(new RpcException(new Status(status, "denied"))));

        var act = async () => await service.EmbedAsync("問い", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(status);
    }

    // 🔴 s2s トークンの取得失敗（ClientCredentialsServiceTokenProvider が投げる InvalidOperationException）も
    // 握り潰さない。匿名で呼ばず、構成不備を故障として上げる。
    [Fact]
    public async Task EmbedAsync_s2s_トークン取得失敗も例外のまま上げる()
    {
        var service = new LlmGatewayGrpcEmbeddingService(
            new ThrowingClient(new InvalidOperationException("ServiceToken:ClientId が未設定です。")));

        var act = async () => await service.EmbedAsync("問い", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed class FakeClient(Pb.EmbedResponse response) : Pb.LlmEmbedding.LlmEmbeddingClient
    {
        public override AsyncUnaryCall<Pb.EmbedResponse> EmbedAsync(
            Pb.EmbedRequest request, CallOptions options) => Fake.UnaryCall(response);
    }

    private sealed class ThrowingClient(Exception exception) : Pb.LlmEmbedding.LlmEmbeddingClient
    {
        public override AsyncUnaryCall<Pb.EmbedResponse> EmbedAsync(
            Pb.EmbedRequest request, CallOptions options) => Fake.ThrowingCall<Pb.EmbedResponse>(exception);
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

// 生成クライアントの偽物が返す AsyncUnaryCall を組む小道具（呼び出し元 2 サービスで同じ形が要る）。
internal static class Fake
{
    public static AsyncUnaryCall<T> UnaryCall<T>(T response) => new(
        Task.FromResult(response),
        Task.FromResult(new Metadata()),
        () => Status.DefaultSuccess,
        () => [],
        () => { });

    public static AsyncUnaryCall<T> ThrowingCall<T>(Exception exception) => new(
        Task.FromException<T>(exception),
        Task.FromResult(new Metadata()),
        () => Status.DefaultSuccess,
        () => [],
        () => { });
}
