using AwesomeAssertions;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Qdrant.Client;
using RetrievalService.Domain;
using RetrievalService.Infrastructure.ExternalServices;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace RetrievalService.Tests.Infrastructure.ExternalServices;

// FR-03, ADR-0016, ADR-0092 決定 2, [[IADR-0467]] (#336): **コレクションごとのクエリ埋め込み。**
//
// 束ねる追加コレクション用の要求だけが `TargetCollection` を名乗り、主コレクション用の要求は
// **従来と同じ意味**（gRPC は空文字＝線に載らない）であることを固定する。
// ［2026-10-10 / #1255］[[IADR-0533]]: REST の埋め込み（`LlmGatewayEmbeddingService`）を撤去したので、REST の本文の表明（旧 T-Q-02）を外した。
// 変異 M-5（主の要求にも名前を載せる）はここで赤になる。
[Trait("TestKind", "Unit")]
public class FusedQueryEmbeddingTests
{
    private const string Voyage = "knowledge_chunks_voyage_3_5";
    // #1746 / [[IADR-0497]]: 語彙索引の既定名（appsettings.json と同じ）。
    private const string Lexical = "knowledge_chunks_lexical";
    private const string Ruri = "knowledge_chunks_ruri_v3";

    // T-Q-01: 要求を作る関数。主は名乗らない・追加は名乗る。
    [Fact]
    public void 主は名乗らず_追加コレクションだけが名乗る()
    {
        QueryEmbeddingRequest.For("問い", new QueryEmbeddingTarget(Voyage)).TargetCollection.Should().BeNull();
        QueryEmbeddingRequest.For("問い", null).TargetCollection.Should().BeNull();
        QueryEmbeddingRequest.For("問い", new QueryEmbeddingTarget(Ruri, NamedInRequest: true))
            .TargetCollection.Should().Be(Ruri);
    }

    // T-Q-03: gRPC の要求。主は target_collection が空（proto3 では線に載らない）・追加は名乗る。
    [Fact]
    public async Task gRPC_主の要求は名乗らず追加の要求だけが名乗る()
    {
        var primaryClient = new CapturingClient(Voyage);
        await new LlmGatewayGrpcEmbeddingService(primaryClient, new QueryEmbeddingTarget(Voyage))
            .EmbedAsync("問い", TestContext.Current.CancellationToken);
        var fusedClient = new CapturingClient(Ruri);
        await new LlmGatewayGrpcEmbeddingService(fusedClient, new QueryEmbeddingTarget(Ruri, NamedInRequest: true))
            .EmbedAsync("問い", TestContext.Current.CancellationToken);

        primaryClient.Last!.TargetCollection.Should().BeEmpty();
        primaryClient.Last.Purpose.Should().Be(Pb.EmbedPurpose.Query);
        fusedClient.Last!.TargetCollection.Should().Be(Ruri);
        fusedClient.Last.Purpose.Should().Be(Pb.EmbedPurpose.Query);
    }

    // T-Q-04: 名乗っても**照合は外さない**。ゲートウェイが別のコレクションを答えたらその系統を捨てる
    // （[[IADR-0422]] 決定 3 の守りは追加コレクションにもそのまま効く）。
    [Fact]
    public async Task 追加コレクションでも答えが食い違えば空ベクトルへ降りる()
    {
        var target = new QueryEmbeddingTarget(Ruri, NamedInRequest: true);

        var grpc = await new LlmGatewayGrpcEmbeddingService(new CapturingClient(Voyage), target)
            .EmbedAsync("問い", TestContext.Current.CancellationToken);

        grpc.Should().BeEmpty();
    }

    // T-Q-05: 追加コレクション名の解決。既定は空・主と同名・空白・重複を捨て、構成の順を保つ。
    [Fact]
    public void 追加コレクション名は空白と主と重複を捨てて順を保つ()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Qdrant:CollectionName"] = Voyage,
            ["Qdrant:FusedCollections:0"] = "",
            ["Qdrant:FusedCollections:1"] = Ruri,
            ["Qdrant:FusedCollections:2"] = Voyage,
            ["Qdrant:FusedCollections:3"] = " other ",
            ["Qdrant:FusedCollections:4"] = Ruri,
        }).Build();

        QdrantVectorStore.ResolveFusedCollectionNames(config).Should().Equal(Ruri, "other");
        QdrantVectorStore.ResolveFusedCollectionNames(new ConfigurationBuilder().Build()).Should().BeEmpty();
    }

    // T-Q-06: 合成点。構成が無ければ**語彙索引だけ**（［2026-10-05 / #1746］[[IADR-0497]] 決定 5。従前は `None`）、
    // 在ればそのコレクションを読むストアと、そのコレクションを名乗る埋め込みの組が組み上がり、語彙索引は最後に付く。
    // 追加コレクションの埋め込みも主と同じ gRPC 実装である（［2026-10-10 / #1255］REST の客体は撤去した）。
    [Fact]
    public void 合成点は構成が無ければ語彙索引だけで_在れば組を作り語彙索引を最後に付ける()
    {
        using (var plain = new ProductionFusedFactory())
        using (var scope = plain.Services.CreateScope())
        {
            var only = scope.ServiceProvider.GetRequiredService<FusedCollections>().Items
                .Should().ContainSingle().Subject;
            only.Collection.Should().Be(Lexical);
            only.LexicalOnly.Should().BeTrue();
            only.Embed.Should().BeSameAs(NoQueryEmbedding.Instance);
            var lexicalStore = only.Store.Should().BeOfType<QdrantVectorStore>().Subject;
            lexicalStore.Collection.Should().Be(Lexical);
            // #1746 監査 🟡3: 語彙索引は取り込みが作るので、無いうちの削除は no-op（IADR-0497 決定 4）。
            lexicalStore.MissingCollectionIsEmpty.Should().BeTrue();
        }

        using var fused = new FusedConfiguredFactory();
        using var fusedScope = fused.Services.CreateScope();
        var items = fusedScope.ServiceProvider.GetRequiredService<FusedCollections>().Items;

        items.Should().HaveCount(2);
        items[1].Collection.Should().Be(Lexical);
        items[1].LexicalOnly.Should().BeTrue();
        items[0].LexicalOnly.Should().BeFalse();
        items[0].Collection.Should().Be(Ruri);
        // 陽性対照: ベクトルの追加コレクションは従来どおり（無ければ削除は例外）。
        items[0].Store.Should().BeOfType<QdrantVectorStore>().Which.MissingCollectionIsEmpty.Should().BeFalse();
        items[0].Store.Should().BeOfType<QdrantVectorStore>().Which.Collection.Should().Be(Ruri);
        items[0].Embed.Should().BeOfType<LlmGatewayGrpcEmbeddingService>();
    }

    // #1746: 本番の合成点を残し、Qdrant のクライアントだけを戻す（追加コレクションの構成なし）。
    private sealed class ProductionFusedFactory : TestWebApplicationFactory
    {
        protected override bool KeepProductionFusedCollections => true;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddSingleton(new QdrantClient("localhost")));
        }
    }

    private sealed class FusedConfiguredFactory : TestWebApplicationFactory
    {
        protected override bool KeepProductionFusedCollections => true;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // 🔴 **`UseSetting` で渡す。** 追加コレクションの名前は `Program.cs` が `builder.Build()` の前に
            // 読む値であり、`ConfigureAppConfiguration` では間に合わない（GraphExpansionFactory と同じ事情）。
            builder.UseSetting("Qdrant:CollectionName", Voyage);
            builder.UseSetting("Qdrant:FusedCollections:0", Ruri);
            // 基底が外した Qdrant クライアントを戻す（組み立てが引く。接続は呼び出しまで起きない）。
            builder.ConfigureServices(services => services.AddSingleton(new QdrantClient("localhost")));
        }
    }

    private sealed class CapturingClient(string answeredCollection) : Pb.LlmEmbedding.LlmEmbeddingClient
    {
        public Pb.EmbedRequest? Last { get; private set; }

        public override AsyncUnaryCall<Pb.EmbedResponse> EmbedAsync(Pb.EmbedRequest request, CallOptions options)
        {
            Last = request;
            var resp = new Pb.EmbedResponse
            {
                Dimensions = 2,
                Model = "m",
                Collection = answeredCollection,
                Embedded = true,
                Endpoint = "e",
                RoutingReason = "r",
            };
            resp.Vector.AddRange([0.5f, 0.25f]);
            return Fake.UnaryCall(resp);
        }
    }
}
