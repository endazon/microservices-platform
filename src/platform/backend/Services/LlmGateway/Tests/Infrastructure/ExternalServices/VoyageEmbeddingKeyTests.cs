using System.Net;
using System.Text;
using AwesomeAssertions;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Ports;
using LlmGateway.Domain.Routing;
using LlmGateway.Features.Embeddings.Embed;
using LlmGateway.Infrastructure.ExternalServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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
        var useCase = new EmbedUseCase(new FixedRouter(decision), services, NullLoggerFactory.Instance,
            new LogOccurrenceThrottle(TimeProvider.System));

        var resp = await useCase.ExecuteAsync(
            new EmbedApiRequest("本文", "internal", EmbedPurpose.Index), TestContext.Current.CancellationToken);

        resp.Embedded.Should().BeFalse();
        resp.Retryable.Should().BeTrue("鍵の欠落は一時障害として扱い、取り込みは再試行の後 DLQ へ送る（恒久スキップにしない）");
        resp.Endpoint.Should().Be("voyage-managed");
        resp.Vector.Should().BeEmpty();
        handler.Calls.Should().Be(0);
    }

    // ---- #1819, IADR-0504 追記: 鍵未設定の失敗でログを埋めない ----------------------------------------

    private sealed class ThrowingProvider(Exception failure) : IEmbeddingProvider
    {
        public int Calls { get; private set; }

        public Task<float[]> EmbedAsync(
            string text, string model, int dimensions, EmbeddingRoutePurpose purpose, CancellationToken ct = default)
        {
            Calls++;
            throw failure;
        }
    }

    private static readonly EmbeddingRoutingDecision VoyageRoute = new(
        Allowed: true, EndpointName: "voyage-managed", Provider: "voyage", Tier: ProtectionTier.B,
        Model: "voyage-3.5", Dimensions: 1024, Collection: "knowledge_chunks_voyage_3_5", Reason: "test");

    private static EmbedUseCase UseCaseWith(
        IEmbeddingProvider provider, RecordingLoggerFactory logs, TimeProvider time)
        => new(new FixedRouter(VoyageRoute),
            new ServiceCollection().AddKeyedSingleton("voyage", provider).BuildServiceProvider(),
            logs, new LogOccurrenceThrottle(time));

    // PoC では取り込みの再試行のたびに鍵未設定の失敗がスタック付きで記録され（1,146 回）、他の行を押し出した。
    // 何回呼んでも Warning 1 行（スタックなし）であり、応答（一時障害＝再試行の後 DLQ）は変わらない。
    [Fact]
    public async Task 鍵なしの埋め込みは何回呼んでもスタックを残さず_5分ごとの要約1行にまとまる()
    {
        var handler = new CountingHandler();
        var logs = new RecordingLoggerFactory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero));
        var useCase = UseCaseWith(
            new VoyageEmbeddingProvider(new SingleClientFactory(handler), Config(null)), logs, time);
        var ct = TestContext.Current.CancellationToken;

        var responses = new List<EmbedApiResponse>();
        for (var i = 0; i < 10; i++)
            responses.Add(await useCase.ExecuteAsync(new EmbedApiRequest("本文", "internal", EmbedPurpose.Index), ct));

        responses.Should().OnlyContain(r => !r.Embedded && r.Retryable,
            "応答は従来どおり一時障害（取り込みは再試行の後 DLQ へ送り、鍵を入れてから再投入できる）");
        handler.Calls.Should().Be(0, "鍵が無いのに Voyage へ要求を送ってはならない");
        logs.Entries.Should().NotContain(e => e.Exception != null, "鍵の欠落でスタックを残さない");
        logs.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
        var first = logs.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Subject;
        first.Message.Should().Contain("Embedding disabled at endpoint voyage-managed")
            .And.Contain("Embedding:Voyage:ApiKey");
        first.Values["Suppressed"].Should().Be(0L);

        time.Advance(LogOccurrenceThrottle.SummaryInterval);
        await useCase.ExecuteAsync(new EmbedApiRequest("本文", "internal", EmbedPurpose.Index), ct);

        var warnings = logs.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        warnings.Should().HaveCount(2);
        warnings[1].Values["Suppressed"].Should().Be(9L, "間隔の間に抑えた 9 件を要約として添える");
    }

    // 対照: 鍵の欠落でない上流の失敗（5xx・通信断）は従来どおり 1 件ずつスタック付きの Error で残す
    // （1 件ごとに状況が違い得るので抑えない）。鍵の欠落だけを型で分けたことの確認。
    [Fact]
    public async Task 鍵の欠落でない上流の失敗は従来どおりスタック付きのErrorで残す()
    {
        var logs = new RecordingLoggerFactory();
        var provider = new ThrowingProvider(new HttpRequestException("boom", null, HttpStatusCode.BadGateway));
        var useCase = UseCaseWith(provider, logs, TimeProvider.System);
        var ct = TestContext.Current.CancellationToken;

        var first = await useCase.ExecuteAsync(new EmbedApiRequest("本文", "internal", EmbedPurpose.Index), ct);
        await useCase.ExecuteAsync(new EmbedApiRequest("本文", "internal", EmbedPurpose.Index), ct);

        first.Retryable.Should().BeTrue();
        logs.Entries.Where(e => e.Level == LogLevel.Error).Should().HaveCount(2)
            .And.OnlyContain(e => e.Exception is HttpRequestException);
    }
}
