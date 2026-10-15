using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using LlmGateway.Tests.Grpc;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace LlmGateway.Tests.Features.Embeddings.Embed;

// FR-03, NFR-16, ADR-0092 決定 2, [[IADR-0467]] (#336): 読み先コレクション（`TargetCollection` /
// `target_collection`）が**輸送を越えて判定器まで届く**ことを、実 Kestrel の h2c で固定する。
// ［2026-10-15 / #1902・[[IADR-0533]]］REST の `/embed` を撤去したので、HTTP/1.1 側の対は外した。
//
// 観測点: 器の構成ではセルフホスト（ruri）が既定どおり**無効**である。したがって
//   - ruri のコレクションを名乗った Query は**拒否**される（名乗りが届いている証拠。届かなければ voyage で埋まる）
//   - voyage のコレクションを名乗った Query は voyage で埋まる（陽性対照）
//   - 名乗らない Query は従来どおり voyage（不変）
// 器は `GrpcEmbedTests` と同じ共有インスタンス（h2c ポートを奪い合わないため。ループバックのみで待つ）。
[Collection(SharedMeterCollection.Name)]
[Trait("TestKind", "Integration")]
public class EmbedTargetCollectionTransportTests
{
    private const string Voyage = "knowledge_chunks_voyage_3_5";
    private const string Ruri = "knowledge_chunks_ruri_v3";
    private readonly GrpcKestrelFactory _factory;

    public EmbedTargetCollectionTransportTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    private static Metadata Bearer() => new()
    {
        {
            "Authorization",
            $"Bearer {GrpcKestrelFactory.IssueToken("service-account-retrieval-service", [PlatformAuthPolicies.ServiceRole])}"
        }
    };

    private Task<Pb.EmbedResponse> GrpcQuery(string target) =>
        new Pb.LlmEmbedding.LlmEmbeddingClient(GrpcChannel.ForAddress(_factory.GrpcAddress)).EmbedAsync(
            new Pb.EmbedRequest { Text = "問い", Purpose = Pb.EmbedPurpose.Query, TargetCollection = target },
            headers: Bearer(), cancellationToken: TestContext.Current.CancellationToken).ResponseAsync;

    // T-T-01: 無効なエンドポイントのコレクションを名乗ると拒否（embedded=false）。
    [Fact]
    public async Task 無効なティアAのコレクションを名乗ると拒否される()
    {
        var grpc = await GrpcQuery(Ruri);

        grpc.Embedded.Should().BeFalse();
        grpc.RoutingReason.Should().Contain(Ruri);
    }

    // T-T-02: 陽性対照。有効なコレクションを名乗れば埋まり、名乗らない要求と同じ答えになる。
    [Fact]
    public async Task 有効なコレクションを名乗れば名乗らないときと同じ答えになる()
    {
        var named = await GrpcQuery(Voyage);
        var unnamed = await GrpcQuery(string.Empty);

        named.Embedded.Should().BeTrue();
        named.Collection.Should().Be(Voyage);
        unnamed.Collection.Should().Be(Voyage);
        named.RoutingReason.Should().Be(unnamed.RoutingReason);
    }
}
