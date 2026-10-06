using System.Net;
using System.Text;
using AwesomeAssertions;
using LlmGateway.Domain.Ports;
using LlmGateway.Domain.Routing;
using LlmGateway.Features.Embeddings.Embed;
using LlmGateway.Infrastructure.ExternalServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;

namespace LlmGateway.Tests.Infrastructure.ExternalServices;

// FR-02, ADR-0016, IADR-0504 (#1764): 埋め込みの鍵（Embedding:Voyage:ApiKey）が「無い」ときと「在る」ときの挙動を固定する。
//
// helm は env Embedding__Voyage__ApiKey を Secret llm-provider-credentials の voyage-api-key から **optional** で渡す
// （キーが Secret に無ければ env が無い＝空）。運用の手順書（docs/operations/voyage-embedding-key-runbook.md）は
// 「鍵なしでも Pod は起動し、埋め込みは一時障害（Retryable）として返り、取り込みが再試行の後 DLQ へ送る」と書く。
// その前提を、外部へ 1 バイトも送らないことと併せてここで固定する。
[Trait("TestKind", "Unit")]
public class VoyageEmbeddingKeyTests
{
    private const string EmbeddingJson = """{"data":[{"embedding":[0.1,0.2,0.3]}]}""";

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(EmbeddingJson, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FixedRouter(EmbeddingRoutingDecision decision) : IEmbeddingRouter
    {
        public EmbeddingRoutingDecision Route(EmbeddingRoutingRequest request) => decision;
    }

    private static IConfiguration Config(string? apiKey)
    {
        var values = new Dictionary<string, string?>();
        if (apiKey is not null) values["Embedding:Voyage:ApiKey"] = apiKey;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    // 鍵が無い（env が無い＝optional のキー欠落）・空・空白のいずれでも、外部へ送らずに失敗する。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 鍵が無ければ外部へ送らずに失敗する(string? apiKey)
    {
        var handler = new CountingHandler();
        var provider = new VoyageEmbeddingProvider(new SingleClientFactory(handler), Config(apiKey));

        var act = () => provider.EmbedAsync("本文", "voyage-3.5", 1024, EmbeddingRoutePurpose.Index, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Embedding:Voyage:ApiKey*");
        handler.Calls.Should().Be(0, "鍵が無いのに Voyage へ要求を送ってはならない");
    }

    // 鍵が在れば Bearer で送る（env の値がそのまま使われる）。
    [Fact]
    public async Task 鍵が在れば_Bearer_で送る()
    {
        var handler = new CountingHandler();
        var provider = new VoyageEmbeddingProvider(new SingleClientFactory(handler), Config("test-key"));

        var vector = await provider.EmbedAsync("本文", "voyage-3.5", 1024, EmbeddingRoutePurpose.Index, TestContext.Current.CancellationToken);

        handler.Calls.Should().Be(1);
        handler.Authorization.Should().Be("Bearer test-key");
        vector.Should().HaveCount(3);
    }

    // 鍵なしの埋め込みは「一時障害」（Embedded=false・Retryable=true）として応答する。
    // 取り込み（DocumentUpdatedConsumer）はこれを恒久スキップにせず再試行し、枯渇したら DLQ へ送る —— 索引に入らない。
    [Fact]
    public async Task 鍵なしの埋め込みは一時障害として応答する()
    {
        var handler = new CountingHandler();
        var services = new ServiceCollection()
            .AddKeyedSingleton<IEmbeddingProvider>("voyage",
                new VoyageEmbeddingProvider(new SingleClientFactory(handler), Config(null)))
            .BuildServiceProvider();
        var decision = new EmbeddingRoutingDecision(
            Allowed: true, EndpointName: "voyage-managed", Provider: "voyage", Tier: ProtectionTier.B,
            Model: "voyage-3.5", Dimensions: 1024, Collection: "knowledge_chunks_voyage_3_5", Reason: "test");
        var useCase = new EmbedUseCase(new FixedRouter(decision), services, NullLoggerFactory.Instance);

        var resp = await useCase.ExecuteAsync(
            new EmbedApiRequest("本文", "internal", EmbedPurpose.Index), TestContext.Current.CancellationToken);

        resp.Embedded.Should().BeFalse();
        resp.Retryable.Should().BeTrue("鍵の欠落は一時障害として扱い、取り込みは再試行の後 DLQ へ送る（恒久スキップにしない）");
        resp.Endpoint.Should().Be("voyage-managed");
        resp.Vector.Should().BeEmpty();
        handler.Calls.Should().Be(0);
    }
}
