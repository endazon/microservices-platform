using AwesomeAssertions;
using IngestionService.Infrastructure.ExternalServices;
using System.Net;
using System.Text;

namespace IngestionService.Tests;

// FR-02, ADR-0013, ADR-0016 (#992): ゲートウェイ `/embed` の応答を**実際に読めること**を固定する。
//
// 🔴 稼働クラスタで踏んだ形はこれである —— `/embed` は 200 と 1024 次元のベクトルを返しているのに、
// 取り込み側は **`Embedded=true` ＋ 空ベクトル**として受け取り、Qdrant が
// `expected dim: 1024, got 0` で拒否していた。**HTTP は成功し、例外も出ない。**
// 契約の項目名が 1 つずれるだけでこの形になり、既存のテストは誰も気づかない
// （消費側のテストは `IEmbeddingService` をスタブに差し替えているため、この結線を一度も通らない）。
public class LlmGatewayEmbeddingServiceTests
{
    // ゲートウェイが実際に返す形（LlmGateway の EmbeddingEndpoints ＋ 既定の Web JSON 規約）。
    // **稼働クラスタから採取した応答をそのまま縮めたもの**であり、手で組んだ想像上の形ではない。
    private const string GatewayResponse = """
        {
          "vector": [0.1, -0.2, 0.3],
          "dimensions": 3,
          "model": "deterministic-hash-v1",
          "collection": "knowledge_chunks_deterministic_v1",
          "embedded": true,
          "endpoint": "deterministic-local",
          "routingReason": "機密区分 Public / 用途 Index",
          "retryable": false
        }
        """;

    private static LlmGatewayEmbeddingService Build(string json) =>
        new(new HttpClient(new StubHandler(json)) { BaseAddress = new Uri("http://llm-gateway") });

    [Fact]
    public async Task EmbedAsync_ゲートウェイ応答のベクトルを落とさずに運ぶ()
    {
        var result = await Build(GatewayResponse).EmbedAsync("本文", "public", TestContext.Current.CancellationToken);

        // 🔴 ここが空になると、索引は「成功したことになって 0 点」になる。
        result.Vector.Should().HaveCount(3);
        result.Embedded.Should().BeTrue();
        result.Collection.Should().Be("knowledge_chunks_deterministic_v1");
        result.Retryable.Should().BeFalse();
    }

    [Fact]
    public async Task EmbedAsync_fail_closed_の応答をそのまま伝える()
    {
        // 送信拒否（機密区分 × ティア）。**再試行しない**（Retryable=false）ことまで含めて写す。
        var result = await Build("""
            {"vector":[],"dimensions":0,"model":"","collection":"","embedded":false,
             "endpoint":null,"routingReason":"fail-closed","retryable":false}
            """).EmbedAsync("本文", "confidential", TestContext.Current.CancellationToken);

        result.Embedded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Vector.Should().BeEmpty();
    }

    [Fact]
    public async Task EmbedAsync_本文が空の応答は一時障害として再試行へ回す()
    {
        var result = await Build("null").EmbedAsync("本文", "public", TestContext.Current.CancellationToken);

        result.Embedded.Should().BeFalse();
        result.Retryable.Should().BeTrue();
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
