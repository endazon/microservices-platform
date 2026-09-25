using System.Net;
using System.Text;
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
// **従来と同じ意味**（REST は `targetCollection: null`・gRPC は空文字＝線に載らない）であることを固定する。
// 変異 M-5（主の要求にも名前を載せる）はここで赤になる。
[Trait("TestKind", "Unit")]
public class FusedQueryEmbeddingTests
{
    private const string Voyage = "knowledge_chunks_voyage_3_5";
    private const string Ruri = "knowledge_chunks_ruri_v3";

    private static string GatewayJson(string collection) => $$"""
        {"vector":[0.5,0.25],"dimensions":2,"model":"m","collection":"{{collection}}","embedded":true,
         "endpoint":"e","routingReason":"r","retryable":false}
        """;

    // T-Q-01: 要求を作る関数（REST と gRPC の共有点）。主は名乗らない・追加は名乗る。
    [Fact]
    public void 主は名乗らず_追加コレクションだけが名乗る()
    {
        QueryEmbeddingRequest.For("問い", new QueryEmbeddingTarget(Voyage)).TargetCollection.Should().BeNull();
        QueryEmbeddingRequest.For("問い", null).TargetCollection.Should().BeNull();
        QueryEmbeddingRequest.For("問い", new QueryEmbeddingTarget(Ruri, NamedInRequest: true))
            .TargetCollection.Should().Be(Ruri);
    }

    // T-Q-02: 🔴 **REST の本文。** 主の要求は `targetCollection` を JSON に**書かない**（従来と同じ本文）。
    [Fact]
    public async Task REST_主の要求本文は従来と同一で追加の要求だけが名乗る()
    {
        var primaryHandler = new CapturingHandler(GatewayJson(Voyage));
        await new LlmGatewayEmbeddingService(
                new HttpClient(primaryHandler) { BaseAddress = new Uri("http://llm-gateway") },
                new QueryEmbeddingTarget(Voyage))
            .EmbedAsync("q", TestContext.Current.CancellationToken);

        var fusedHandler = new CapturingHandler(GatewayJson(Ruri));
        await new LlmGatewayEmbeddingService(
                new HttpClient(fusedHandler) { BaseAddress = new Uri("http://llm-gateway") },
                new QueryEmbeddingTarget(Ruri, NamedInRequest: true))
            .EmbedAsync("q", TestContext.Current.CancellationToken);

        // 主は名乗らない（null ＝受け側は未指定として従来どおり優先度順に選ぶ）。ASCII の問いで本文ごと比べる。
        primaryHandler.Body.Should().Be(
            """{"text":"q","confidentiality":null,"purpose":1,"targetCollection":null}""");
        fusedHandler.Body.Should().Be(
            $$"""{"text":"q","confidentiality":null,"purpose":1,"targetCollection":"{{Ruri}}"}""");
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
    // （[[IADR-0422]] 決定 3 の守りは追加コレクションにもそのまま効く）。REST / gRPC で同じ答え。
    [Fact]
    public async Task 追加コレクションでも答えが食い違えば空ベクトルへ降りる()
    {
        var target = new QueryEmbeddingTarget(Ruri, NamedInRequest: true);

        var rest = await new LlmGatewayEmbeddingService(
                new HttpClient(new CapturingHandler(GatewayJson(Voyage))) { BaseAddress = new Uri("http://llm-gateway") },
                target)
            .EmbedAsync("問い", TestContext.Current.CancellationToken);
        var grpc = await new LlmGatewayGrpcEmbeddingService(new CapturingClient(Voyage), target)
            .EmbedAsync("問い", TestContext.Current.CancellationToken);

        rest.Should().BeEmpty();
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

    // T-Q-06: 合成点。構成が無ければ `None`（既定＝従来と同一）、在ればそのコレクションを読む
    // ストアと、そのコレクションを名乗る埋め込みの組が組み上がる。REST の客体は主と**同じ名前つき
    // クライアント**（同じ宛先）を使う。
    [Fact]
    public void 合成点は構成が無ければNoneで_在れば組を作る()
    {
        using (var plain = new TestWebApplicationFactory())
        using (var scope = plain.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<FusedCollections>().Items.Should().BeEmpty();

        using var fused = new FusedConfiguredFactory();
        using var fusedScope = fused.Services.CreateScope();
        var items = fusedScope.ServiceProvider.GetRequiredService<FusedCollections>().Items;

        items.Should().ContainSingle();
        items[0].Collection.Should().Be(Ruri);
        items[0].Store.Should().BeOfType<QdrantVectorStore>().Which.Collection.Should().Be(Ruri);
        items[0].Embed.Should().BeOfType<LlmGatewayEmbeddingService>();
        fusedScope.ServiceProvider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(LlmGatewayEmbeddingService.HttpClientName).BaseAddress
            // 基底の構成（`Services:LlmGateway`）で主の型つきクライアントが向く先と同じである。
            .Should().Be(new Uri("http://localhost:5007"));
    }

    private sealed class FusedConfiguredFactory : TestWebApplicationFactory
    {
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

    private sealed class CapturingHandler(string json) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
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
