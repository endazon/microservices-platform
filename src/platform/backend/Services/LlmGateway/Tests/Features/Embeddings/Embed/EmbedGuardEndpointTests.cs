using System.Net.Http.Headers;
using AwesomeAssertions;
using Grpc.Core;
using LlmGateway.Domain.Authorization;
using LlmGateway.Domain.Limits;
using LlmGateway.Domain.Ports;
using LlmGateway.Domain.Routing;
using LlmGateway.Tests.Grpc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace LlmGateway.Tests.Features.Embeddings.Embed;

// NFR-18, NFR-21, FR-02, IADR-0684, IADR-0678, IADR-0701: 埋め込みの gRPC 面（LlmEmbedding/Embed）に、呼び手ごとの許可表と
// 送る前の上限が掛かることを固定する。プロバイダへ届いたかは、呼び出し回数を数えるスパイで見る。
// 値は文字列で書く（設定キーは配備が読む外部の契約であるため、定数を引かずに綴りを固定する）。
[Collection(SharedMeterCollection.Name)]
[Trait("TestKind", "Integration")]
public class EmbedGuardEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private sealed class SpyEmbeddingProvider : IEmbeddingProvider
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<float[]> EmbedAsync(string text, string model, int dimensions, EmbeddingRoutePurpose purpose, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new float[dimensions]);
        }
    }

    private WebApplicationFactory<Program> With(SpyEmbeddingProvider voyage, params (string Key, string Value)[] settings) =>
        factory.WithWebHostBuilder(b => b
            .ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value)))
            .ConfigureServices(s =>
            {
                s.RemoveAll<IEmbeddingProvider>();
                s.AddKeyedSingleton<IEmbeddingProvider>("voyage", voyage);
            }));

    // 取引ユニットの呼び手（既定の許可表に在るが、埋め込みは許可されていない）。
    private static HttpClient AsTradingCaller(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestServiceTokens.IssueToken("service-account-ai-stock-trading-llm-caller", [PlatformAuthPolicies.ServiceRole]));
        return client;
    }

    private static readonly EmbedApiRequest Public = new("本文", "public", EmbedPurpose.Index);

    [Fact]
    public async Task 埋め込みの許可が無い呼び手は段2でPERMISSION_DENIED_プロバイダへ届かない()
    {
        var voyage = new SpyEmbeddingProvider();
        using var app = With(voyage, ("Llm:CallerPolicy:EmbeddingMode", "Enforce"));

        var act = () => AsTradingCaller(app).EmbedAsync(Public, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        voyage.Calls.Should().Be(0);

        // 陽性対照: 許可のある呼び手（既定のクライアント＝retrieval-service）は同じ構成で埋め込む。
        var ok = await app.CreateClient().EmbedAsync(Public, TestContext.Current.CancellationToken);
        ok.Embedded.Should().BeTrue();
        voyage.Calls.Should().Be(1);
    }

    [Fact]
    public async Task 既定の段では許可の無い呼び手も埋め込む()
    {
        var voyage = new SpyEmbeddingProvider();
        using var app = With(voyage);

        var body = await AsTradingCaller(app).EmbedAsync(Public, TestContext.Current.CancellationToken);

        body.Embedded.Should().BeTrue("段 1（既定）は報告だけで止めない");
        voyage.Calls.Should().Be(1);
    }

    [Fact]
    public async Task 段2では埋め込みの要求数の上限を超えたら送らず_再試行できる縮退で返す()
    {
        var voyage = new SpyEmbeddingProvider();
        using var app = With(voyage,
            ("Llm:CallLimits:EmbeddingMode", "Enforce"),
            ("Llm:CallLimits:Default:DailyCostLimit", "1000"),
            ("Llm:CallLimits:Default:PurposeRequestsPerMinute", "1"));
        var client = app.CreateClient();

        (await client.EmbedAsync(Public, TestContext.Current.CancellationToken)).Embedded.Should().BeTrue();
        var limited = await client.EmbedAsync(Public, TestContext.Current.CancellationToken);

        limited.Embedded.Should().BeFalse();
        limited.Retryable.Should().BeTrue("要求数の上限は時間が経てば解ける");
        limited.RoutingReason.Should().Contain("purpose_rate");
        voyage.Calls.Should().Be(1, "上限で止めた呼び出しはプロバイダへ届かない");
    }

    [Fact]
    public async Task 段2では入力の長さの上限を超えた埋め込みを送らず_再試行しない縮退で返す()
    {
        var voyage = new SpyEmbeddingProvider();
        using var app = With(voyage,
            ("Llm:CallLimits:EmbeddingMode", "Enforce"),
            ("Llm:CallLimits:Default:DailyCostLimit", "1000"),
            ("Llm:CallLimits:MaxPromptChars", "3"));

        var body = await app.CreateClient().EmbedAsync(
            new EmbedApiRequest("四文字だ", "public", EmbedPurpose.Index), TestContext.Current.CancellationToken);

        body.Embedded.Should().BeFalse();
        body.Retryable.Should().BeFalse("長さは再試行で解消しない");
        voyage.Calls.Should().Be(0);
    }

    [Fact]
    public async Task 段2では当日の費用の上限を超えた埋め込みを送らない()
    {
        var voyage = new SpyEmbeddingProvider();
        // 実設定の voyage-3.5 の計上単価（0.06 / 百万トークン）で、2 文字 = 0.00000012。上限はその 1.5 倍。
        using var app = With(voyage,
            ("Llm:CallLimits:EmbeddingMode", "Enforce"),
            ("Llm:CallLimits:Default:DailyCostLimit", "0.00000018"));
        var client = app.CreateClient();

        (await client.EmbedAsync(new EmbedApiRequest("本文", "public", EmbedPurpose.Index), TestContext.Current.CancellationToken))
            .Embedded.Should().BeTrue();
        var limited = await client.EmbedAsync(new EmbedApiRequest("本文", "public", EmbedPurpose.Index), TestContext.Current.CancellationToken);

        limited.Embedded.Should().BeFalse();
        limited.RoutingReason.Should().Contain("caller_cost");
        voyage.Calls.Should().Be(1);
    }

    // AC9: 既定の設定（appsettings.json）は、埋め込みを取り込み・検索の 2 呼び手だけに許し、取引ユニットの呼び手には許さない。
    // 標識を引き継ぐのは AI 分析・検索の 2 呼び手だけ（ゲートウェイへ標識を運ぶ正規の呼び手）。
    [Fact]
    public void 既定の設定では埋め込みの許可は取り込みと検索だけ_標識の許可集合はAI分析と検索だけ()
    {
        var policy = factory.Services.GetRequiredService<IOptions<LlmCallerPolicyOptions>>().Value;
        policy.EmbeddingMode.Should().Be(LlmGuardStage.Report);
        policy.Callers.Where(c => c.Value.AllowEmbedding).Select(c => c.Key)
            .Should().BeEquivalentTo(["retrieval-service", "ingestion-service"]);
        policy.Callers["ai-stock-trading-llm-caller"].AllowEmbedding.Should().BeFalse();

        var marker = factory.Services.GetRequiredService<IOptions<LlmSyntheticMarkerOptions>>().Value;
        marker.Mode.Should().Be(LlmSyntheticMarkerMode.Report);
        marker.TrustedCallers.Should().BeEquivalentTo(["aianalysis-service", "retrieval-service"]);

        var limits = factory.Services.GetRequiredService<IOptions<LlmCallLimitOptions>>().Value;
        limits.EmbeddingMode.Should().Be(LlmGuardStage.Report);
        limits.EmbeddingPrices.Should().ContainKey("voyage-3.5");
    }
}
