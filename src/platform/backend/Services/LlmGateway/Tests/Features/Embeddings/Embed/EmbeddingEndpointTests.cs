using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;
using LlmGateway.Domain.Ports;
using LlmGateway.Domain.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Http.Json;

namespace LlmGateway.Tests.Features.Embeddings.Embed;

// FR-02, FR-05, ADR-0016, ADR-0017: /embed が機密区分・用途に応じて送信先を切り替え、
// 高機密（confidential/restricted）は外部埋め込み API へ送信しない（fail-closed）ことを検証する。
public class EmbeddingEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    // 呼び出し回数を記録する埋め込みプロバイダ（外部未送信の検証用）。
    private sealed class SpyEmbeddingProvider(int returnDimensions) : IEmbeddingProvider
    {
        public int Calls { get; private set; }

        public Task<float[]> EmbedAsync(string text, string model, int dimensions, EmbeddingRoutePurpose purpose, CancellationToken ct = default)
        {
            Calls++;
            // returnDimensions>=0 なら指定サイズ、-1 は要求次元どおりを返す。
            var size = returnDimensions >= 0 ? returnDimensions : dimensions;
            return Task.FromResult(new float[size]);
        }
    }

    private HttpClient ClientWith(SpyEmbeddingProvider voyage, SpyEmbeddingProvider ruri,
        Action<EmbeddingRoutingOptions>? configure = null)
        => factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IEmbeddingProvider>();
            s.AddKeyedSingleton<IEmbeddingProvider>("voyage", voyage);
            s.AddKeyedSingleton<IEmbeddingProvider>("selfhosted-embedding", ruri);
            if (configure is not null)
                s.Configure(configure);
        })).CreateClient();

    // public は既定外部経路（voyage・ティアB・1024次元）へ索引される（スタブ解消）。
    [Fact]
    public async Task PostEmbed_Public_RoutesToVoyage_1024()
    {
        var voyage = new SpyEmbeddingProvider(1024);
        var ruri = new SpyEmbeddingProvider(768);
        var client = ClientWith(voyage, ruri);

        var resp = await client.PostAsJsonAsync("/embed",
            new EmbedApiRequest("本文", "public", EmbedPurpose.Index), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<EmbedApiResponse>(TestContext.Current.CancellationToken);
        body!.Embedded.Should().BeTrue();
        body.Dimensions.Should().Be(1024);
        body.Collection.Should().Be("knowledge_chunks_voyage_3_5");
        body.Endpoint.Should().Be("voyage-managed");
        voyage.Calls.Should().Be(1);
        ruri.Calls.Should().Be(0);
    }

    // confidential はティアA固定。セルフホスト未有効（既定）なら fail-closed で外部（voyage）へ送信しない。
    [Theory]
    [InlineData("confidential")]
    [InlineData("restricted")]
    public async Task PostEmbed_HighSensitivity_SelfHostedDisabled_FailClosed_NoExternalCall(string confidentiality)
    {
        var voyage = new SpyEmbeddingProvider(1024);
        var ruri = new SpyEmbeddingProvider(768);
        var client = ClientWith(voyage, ruri); // 既定 appsettings では selfhosted-ruri は Enabled=false

        var resp = await client.PostAsJsonAsync("/embed",
            new EmbedApiRequest("極秘の本文", confidentiality, EmbedPurpose.Index), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<EmbedApiResponse>(TestContext.Current.CancellationToken);
        body!.Embedded.Should().BeFalse();       // 索引しない
        body.Vector.Should().BeEmpty();
        body.RoutingReason.Should().Contain("拒否");
        // 最重要: 外部（ティアB=voyage）は一切呼ばれない（本文が外部へ送信されない）。
        voyage.Calls.Should().Be(0);
        ruri.Calls.Should().Be(0);
    }

    // confidentiality 未指定は安全側（restricted 相当）に倒し、セルフホスト未有効なら fail-closed。
    [Fact]
    public async Task PostEmbed_WithoutConfidentiality_FallsBackToRestricted_FailClosed()
    {
        var voyage = new SpyEmbeddingProvider(1024);
        var ruri = new SpyEmbeddingProvider(768);
        var client = ClientWith(voyage, ruri);

        var resp = await client.PostAsJsonAsync("/embed",
            new EmbedApiRequest("区分不明の本文"), TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadFromJsonAsync<EmbedApiResponse>(TestContext.Current.CancellationToken);
        body!.Embedded.Should().BeFalse();
        voyage.Calls.Should().Be(0);
    }

    // confidential はセルフホスト有効時のみティアA（ruri・768次元）へ索引。外部（voyage）は呼ばれない。
    [Fact]
    public async Task PostEmbed_Confidential_SelfHostedEnabled_RoutesToSelfHosted_NoExternalCall()
    {
        var voyage = new SpyEmbeddingProvider(1024);
        var ruri = new SpyEmbeddingProvider(768);
        var client = ClientWith(voyage, ruri, configure: o =>
        {
            // セルフホスト（ティアA）を有効化する。
            o.Endpoints =
            [
                new EmbeddingEndpointOptions
                {
                    Name = "voyage-managed", Tier = ProtectionTier.B, Provider = "voyage",
                    Model = "voyage-3.5", Dimensions = 1024, Collection = "knowledge_chunks_voyage_3_5",
                    Enabled = true, Priority = 10
                },
                new EmbeddingEndpointOptions
                {
                    Name = "selfhosted-ruri", Tier = ProtectionTier.A, Provider = "selfhosted-embedding",
                    Model = "ruri-v3", Dimensions = 768, Collection = "knowledge_chunks_ruri_v3",
                    Enabled = true, Priority = 20
                }
            ];
        });

        var resp = await client.PostAsJsonAsync("/embed",
            new EmbedApiRequest("極秘の本文", "confidential", EmbedPurpose.Index), TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadFromJsonAsync<EmbedApiResponse>(TestContext.Current.CancellationToken);
        body!.Embedded.Should().BeTrue();
        body.Dimensions.Should().Be(768);
        body.Collection.Should().Be("knowledge_chunks_ruri_v3");
        body.Endpoint.Should().Be("selfhosted-ruri");
        ruri.Calls.Should().Be(1);
        voyage.Calls.Should().Be(0);      // 外部へは送らない
    }

    // クエリ埋め込みは機密区分に依らず既定外部経路（voyage・1024次元）へ。
    [Fact]
    public async Task PostEmbed_Query_RoutesToVoyage()
    {
        var voyage = new SpyEmbeddingProvider(1024);
        var ruri = new SpyEmbeddingProvider(768);
        var client = ClientWith(voyage, ruri);

        var resp = await client.PostAsJsonAsync("/embed",
            new EmbedApiRequest("検索クエリ", Confidentiality: null, Purpose: EmbedPurpose.Query), TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadFromJsonAsync<EmbedApiResponse>(TestContext.Current.CancellationToken);
        body!.Embedded.Should().BeTrue();
        body.Dimensions.Should().Be(1024);
        body.Collection.Should().Be("knowledge_chunks_voyage_3_5");
        voyage.Calls.Should().Be(1);
    }

    // 次元不整合のベクトルは索引しない（fail-closed）。誤次元をモデル別コレクションへ書かない。
    [Fact]
    public async Task PostEmbed_DimensionMismatch_FailClosed()
    {
        var voyage = new SpyEmbeddingProvider(512); // 期待 1024 と異なる次元を返す
        var ruri = new SpyEmbeddingProvider(768);
        var client = ClientWith(voyage, ruri);

        var resp = await client.PostAsJsonAsync("/embed",
            new EmbedApiRequest("本文", "public", EmbedPurpose.Index), TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadFromJsonAsync<EmbedApiResponse>(TestContext.Current.CancellationToken);
        body!.Embedded.Should().BeFalse();
        body.RoutingReason.Should().Contain("次元");
    }
}
