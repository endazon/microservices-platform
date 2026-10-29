using IngestionService.Common.Concurrency;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using IngestionService.Common.Observability;
using IngestionService.Domain.Ports;
using IngestionService.Infrastructure.ExternalServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

namespace IngestionService.Tests.Infrastructure.ExternalServices;

// FR-04, FR-05, SC-01, SC-08, [[IADR-0502]] 決定 2 (i)・3 (#1760): 起動時の器がキーワード索引の 2 つの経路を呼ぶこと。
//   - ブートストラップ（`QdrantBootstrapHostedService`）が集合値キーの索引を張る（全文索引の後）。
//   - 発見（`QdrantKeywordIndexDiscoveryHostedService`）が既存の点の属性キーへ張り、失敗してもホストを止めない。
[Trait("TestKind", "Unit")]
public class QdrantKeywordIndexHostedServiceTests
{
    // T-39: ブートストラップは全文索引に続けてキーワード索引を張る（［2026-10-20 / #1993］続けて `document_id` の索引も）。
    [Fact]
    public async Task Bootstrap_EnsuresKeywordIndexesAfterFullTextIndexes()
    {
        var store = new RecordingStore();
        var state = new QdrantBootstrapState();
        var bootstrap = NewBootstrap(store, state, new TestMeterFactory());

        await bootstrap.StartAsync(TestContext.Current.CancellationToken);
        await bootstrap.ExecuteTask!;

        store.Calls.Should().Equal(["collections", "cjk", "keyword", "document-id"]);
        state.IsEnsured.Should().BeTrue();
    }

    // FR-02, FR-03, [[IADR-0650]] (#2283) 受け入れ基準 1: 起動時に Qdrant へ届かなくても、届くようになれば
    // コレクションと索引が作られる（従前は 1 回だけ試して諦め、Pod の生存中ずっと書き込みが失敗した）。
    [Fact]
    public async Task Bootstrap_RetriesUntilQdrantIsReachable_ThenEnsuresEverything()
    {
        var store = new RecordingStore { FailEnsureTimes = 3 };
        var state = new QdrantBootstrapState();
        using var meters = new TestMeterFactory();
        long failures = 0;
        using var listener = Listen(meters, QdrantBootstrapMetrics.FailureCounterName, v => Interlocked.Add(ref failures, v));
        var bootstrap = NewBootstrap(store, state, meters);

        await bootstrap.StartAsync(TestContext.Current.CancellationToken);
        await bootstrap.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        store.Calls.Should().Equal(["fail", "fail", "fail", "collections", "cjk", "keyword", "document-id"]);
        state.IsEnsured.Should().BeTrue();
        // 受け入れ基準 3: 失敗は計器で数える（回数と一致する）。
        Interlocked.Read(ref failures).Should().Be(3);
    }

    // 受け入れ基準 2: 保証が成功するまで readiness は Unhealthy（陰性）、成功したら Healthy（陽性の対照）。
    [Fact]
    public async Task BootstrapHealthCheck_IsUnhealthyUntilEnsured()
    {
        var state = new QdrantBootstrapState();
        var check = new QdrantBootstrapHealthCheck(state);
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(QdrantBootstrapHealthCheck.Name, check, HealthStatus.Unhealthy, null),
        };

        (await check.CheckHealthAsync(context, TestContext.Current.CancellationToken)).Status
            .Should().Be(HealthStatus.Unhealthy);

        state.MarkEnsured();

        (await check.CheckHealthAsync(context, TestContext.Current.CancellationToken)).Status
            .Should().Be(HealthStatus.Healthy);
    }

    // 再試行の待ちは倍々で、上限で頭打ちになる（Qdrant を叩き続けない・待ちすぎない）。
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(1000, 30)]
    public void Backoff_DoublesAndCaps(int failures, int expectedSeconds) =>
        QdrantBootstrapHostedService.Backoff.Default.After(failures).Should().Be(TimeSpan.FromSeconds(expectedSeconds));

    // 停止要求で再試行のループを抜ける（ホストの停止を塞がない）。
    [Fact]
    public async Task Bootstrap_StopsRetryingOnShutdown()
    {
        var store = new RecordingStore { FailEnsureTimes = int.MaxValue };
        var state = new QdrantBootstrapState();
        var bootstrap = new QdrantBootstrapHostedService(Provider(store),
            NullLogger<QdrantBootstrapHostedService>.Instance, state, new QdrantBootstrapMetrics(new TestMeterFactory()),
            new QdrantBootstrapHostedService.Backoff(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10)));

        await bootstrap.StartAsync(TestContext.Current.CancellationToken);
        // 1 回失敗して長い待ちに入ったところで止める（ループの中の待ちが停止で解けることを見る）。
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!store.Calls.Contains("fail") && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        store.Calls.Should().Contain("fail");

        await bootstrap.StopAsync(TestContext.Current.CancellationToken);

        bootstrap.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue("待ちの取り消しで例外を漏らさず抜ける");
        state.IsEnsured.Should().BeFalse();
    }

    // FR-02, [[IADR-0650]] (#2283): 後続の器（属性キーの発見）は保証の成功を待ってから始める。
    [Fact]
    public async Task Discovery_WaitsForBootstrap()
    {
        var store = new RecordingStore();
        var state = new QdrantBootstrapState();
        var discovery = new QdrantKeywordIndexDiscoveryHostedService(Provider(store),
            NullLogger<QdrantKeywordIndexDiscoveryHostedService>.Instance, state);

        await discovery.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        store.Calls.Should().BeEmpty("保証が成功するまで始めない");

        state.MarkEnsured();
        await discovery.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        store.Calls.Should().Equal(["discovery"]);
    }

    // T-39: 発見の器は既存の点からの発見を呼ぶ。失敗はここで捕まえる（ホストを止めない）。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_RunsInBackground_AndSwallowsFailures(bool fail)
    {
        var store = new RecordingStore { FailDiscovery = fail };
        var discovery = new QdrantKeywordIndexDiscoveryHostedService(Provider(store),
            NullLogger<QdrantKeywordIndexDiscoveryHostedService>.Instance, QdrantBootstrapStateForTests.Ensured());

        await discovery.StartAsync(TestContext.Current.CancellationToken);
        await (discovery.ExecuteTask ?? Task.CompletedTask);

        store.Calls.Should().Equal(["discovery"]);
        discovery.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue("例外でホストを止めない");
    }

    private static IServiceProvider Provider(IIngestionVectorStore store) =>
        new ServiceCollection().AddSingleton(store).BuildServiceProvider();

    private static QdrantBootstrapHostedService NewBootstrap(IIngestionVectorStore store, QdrantBootstrapState state,
        IMeterFactory meters) =>
        new(Provider(store), NullLogger<QdrantBootstrapHostedService>.Instance, state, new QdrantBootstrapMetrics(meters),
            new QdrantBootstrapHostedService.Backoff(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(5)));

    private static MeterListener Listen(TestMeterFactory factory, string instrumentName, Action<long> onMeasurement)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Scope == factory && instrument.Name == instrumentName)
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => onMeasurement(value));
        listener.Start();
        return listener;
    }

    private sealed class RecordingStore : IIngestionVectorStore
    {
        internal List<string> Calls { get; } = [];
        internal bool FailDiscovery { get; init; }

        // 最初の N 回のコレクションの保証を失敗させる（Qdrant に届かない起動の模擬。#2283）。
        internal int FailEnsureTimes { get; set; }

        public Task EnsureCollectionsAsync(CancellationToken ct = default)
        {
            if (FailEnsureTimes > 0)
            {
                FailEnsureTimes--;
                Calls.Add("fail");
                return Task.FromException(new InvalidOperationException("simulated: Qdrant unreachable"));
            }
            Calls.Add("collections");
            return Task.CompletedTask;
        }

        public Task EnsureCjkNgramIndexAsync(CancellationToken ct = default)
        {
            Calls.Add("cjk");
            return Task.CompletedTask;
        }

        public Task EnsureKeywordIndexesAsync(CancellationToken ct = default)
        {
            Calls.Add("keyword");
            return Task.CompletedTask;
        }

        // FR-06, SC-05, [[IADR-0565]] 決定 2 (#1993): 索引の状態の照会が引く `document_id` の索引。
        public Task EnsureDocumentIdIndexAsync(CancellationToken ct = default)
        {
            Calls.Add("document-id");
            return Task.CompletedTask;
        }

        public Task<int> EnsureKeywordIndexesForExistingPointsAsync(CancellationToken ct = default)
        {
            Calls.Add("discovery");
            return FailDiscovery
                ? Task.FromException<int>(new InvalidOperationException("simulated"))
                : Task.FromResult(1);
        }

        public Task UpsertChunkAsync(string collection, Guid chunkId, Guid documentId, string title, string text,
            int chunkIndex, float[] vector, string? markdownUri, Dictionary<string, string> attributes,
            List<string> tags, DateTimeOffset? updatedAt = null, List<string>? sharedWith = null,
            long? sequence = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task UpsertMetadataPointAsync(string collection, Guid pointId, Guid documentId, string title,
            string indexText, float[] vector, string? markdownUri, Dictionary<string, string> attributes,
            List<string> tags, DateTimeOffset? updatedAt = null, List<string>? sharedWith = null,
            long? sequence = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task UpsertLexicalChunkAsync(Guid chunkId, Guid documentId, string title, string text,
            int chunkIndex, string? markdownUri, Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null,
            long? sequence = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task UpsertLexicalMetadataPointAsync(Guid pointId, Guid documentId, string title,
            string indexText, string? markdownUri, Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null,
            long? sequence = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteByDocumentFromAllAsync(Guid documentId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
