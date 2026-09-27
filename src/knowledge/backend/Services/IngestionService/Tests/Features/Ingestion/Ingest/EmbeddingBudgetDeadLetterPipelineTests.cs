using System.Reflection;
using AwesomeAssertions;
using IngestionService.Domain;
using IngestionService.Domain.Ports;
using IngestionService.Features.Ingestion.Ingest;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Messaging;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;

namespace IngestionService.Tests.Features.Ingestion.Ingest;

// FR-02 テスト仕様 T-22 (#1640) —— UC-04, ADR-0027: **埋め込みの総枠を使い切った文書は再試行せずデッドレターへ。**
//
// 🔴 Wolverine の実配送（ローカルキュー・本番と同じ共通既定 `UsePlatformMessagingDefaults` ＋ 方針）で測る。
// 受け口（チェーン）の規則が全体の既定（4 試行・2/10/30 秒の再試行）より先に効くことは、規則の評価だけでは確かめられない。
//   - 総枠の使い切り: 1 回目の試行でデッドレターへ（試行 1 回・本文の取得 1 回）。方針を外す変異では 2 秒後に再試行が回り、
//     追跡の期限（20 秒）までに終わらないか、試行が 2 回以上になって赤になる。
//   - 対照: 呼び出しごとの時間切れ（`embedding`）は再試行される（1 回目で時間切れ → 2 秒後の 2 回目で成功）。
[Trait("TestKind", "Integration")]
public class EmbeddingBudgetDeadLetterPipelineTests
{
    private static DocumentUpdated Event() => new(
        Guid.NewGuid(), "総枠の試験", "normalized", "storage://bucket/big.md",
        new Dictionary<string, string> { ["confidentiality"] = "internal" }, ["ops"], DateTimeOffset.UtcNow);

    private static string ManySections(int count) =>
        string.Join("\n\n", Enumerable.Range(1, count).Select(i => $"# 節 {i}\n\n本文 {i}"));

    private static IHost BuildHost(CountingReader reader, IEmbeddingService embed, FakeTimeProvider clock,
        IngestionTimeouts timeouts)
        => new HostBuilder()
            .UseWolverine(opts =>
            {
                opts.AddPlatformWolverineStep<DocumentUpdatedConsumer>(new PipelineOptions());
                opts.UsePlatformMessagingDefaults();
                opts.PublishMessage<DocumentUpdated>().ToLocalQueue("ingest-budget-probe");
                opts.Policies.Add(new EmbeddingBudgetDeadLetterPolicy());
            })
            .ConfigureServices(services => services
                .AddLogging()
                .AddPlatformConsumerTimeouts()
                .AddSingleton(timeouts)
                .AddSingleton<TimeProvider>(clock)
                .AddSingleton<IDocumentContentReader>(reader)
                .AddSingleton<IChunkingService, MarkdownChunkingService>()
                .AddSingleton(embed)
                .AddSingleton<IIngestionVectorStore, NoopStore>()
                .AddSingleton<IIngestionCompletedPublisher, NoopStore>()
                .DisableAllExternalWolverineTransports())
            .Build();

    [Fact]
    public async Task 総枠を使い切った文書は一回目の試行でデッドレターへ送る()
    {
        var clock = new FakeTimeProvider();
        var reader = new CountingReader(ManySections(10));
        var embed = new ClockAdvancingEmbedder(clock, TimeSpan.FromSeconds(1));
        using var host = BuildHost(reader, embed, clock,
            IngestionTimeouts.Default with { EmbeddingBudget = TimeSpan.FromSeconds(3) });
        await host.StartAsync(TestContext.Current.CancellationToken);

        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(TimeSpan.FromSeconds(20))
            .PublishMessageAndWaitAsync(Event());
        await host.StopAsync(TestContext.Current.CancellationToken);

        var dead = session.MovedToErrorQueue.SingleEnvelope<DocumentUpdated>();
        dead.Attempts.Should().Be(1, "総枠の使い切りは再試行しない");
        reader.Calls.Should().Be(1);
        embed.Calls.Should().Be(3, "総枠 3 秒・1 回 1 秒で 3 回呼んだ後の判定で止まる");
    }

    // 対照: 呼び出しごとの時間切れは一時的な障害として再試行される（デッドレターへ直行しない）。
    [Fact]
    public async Task 呼び出しごとの時間切れは再試行される()
    {
        var clock = new FakeTimeProvider();
        var reader = new CountingReader("# 見出し\n\n本文");
        var embed = new HangsOnFirstCallEmbedder();
        using var host = BuildHost(reader, embed, clock,
            IngestionTimeouts.Default with { Embedding = TimeSpan.FromSeconds(1) });
        await host.StartAsync(TestContext.Current.CancellationToken);

        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(TimeSpan.FromSeconds(30))
            .PublishMessageAndWaitAsync(Event());
        await host.StopAsync(TestContext.Current.CancellationToken);

        session.MovedToErrorQueue.Envelopes().Should().BeEmpty();
        reader.Calls.Should().Be(2, "1 回目は埋め込みの時間切れ、2 回目で成功する");
    }

    // 本番の Program.cs が `DocumentUpdated` の受け口へ方針を入れている（受け口の規則が総枠の使い切りをデッドレターへ送る）。
    [Fact]
    public void 本番の配線は受け口に総枠の使い切りをデッドレターへ送る規則を入れる()
    {
        using var factory = new IntrospectionEndpointTests.Factory();
        var chain = factory.Services.GetRequiredService<HandlerGraph>().Chains
            .Single(c => c.MessageType == typeof(DocumentUpdated));
        var budget = new ConsumerTimeoutException(DocumentUpdatedConsumer.StepName,
            IngestionTimeouts.EmbeddingBudgetTarget, TimeSpan.FromSeconds(300), "x");

        // 受け口の規則が在ること（方針が無ければ 0 件）。規則の数は内部の集合から数える（公開の列挙口が無い）。
        typeof(FailureRuleCollection).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(f => f.GetValue(chain.Failures)).OfType<System.Collections.ICollection>()
            .Sum(c => c.Count).Should().BeGreaterThan(0, "方針が受け口に規則を足している");
        ContinuationNameAt(chain.Failures, budget).Should().Be("MoveToErrorQueue");
    }

    private static string? ContinuationNameAt(FailureRuleCollection failures, Exception exception)
        => (typeof(FailureRuleCollection).GetMethod("DetermineExecutionContinuation",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("FailureRuleCollection.DetermineExecutionContinuation が見つからない"))
            .Invoke(failures, [exception, new Envelope { Attempts = 1 }])?.GetType().Name;

    public sealed class CountingReader(string body) : IDocumentContentReader
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<string> ReadAsync(string markdownUri, string title, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(body);
        }
    }

    private sealed class ClockAdvancingEmbedder(FakeTimeProvider clock, TimeSpan perCall) : IEmbeddingService
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<EmbeddingResult> EmbedAsync(string text, string? confidentiality, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            clock.Advance(perCall);
            return Task.FromResult(new EmbeddingResult(new float[4], "c", true));
        }
    }

    // 1 回目は ct が立つまで返らず（呼び出しごとの期限で時間切れ）、2 回目以降は即時に成功する。
    private sealed class HangsOnFirstCallEmbedder : IEmbeddingService
    {
        private int _calls;

        public async Task<EmbeddingResult> EmbedAsync(string text, string? confidentiality, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                await Task.Delay(Timeout.Infinite, ct);
            return new EmbeddingResult(new float[4], "c", true);
        }
    }

    private sealed class NoopStore : IIngestionVectorStore, IIngestionCompletedPublisher
    {
        public Task EnsureCollectionsAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpsertChunkAsync(string collection, Guid chunkId, Guid documentId, string title,
            string text, int chunkIndex, float[] vector, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UpsertMetadataPointAsync(string collection, Guid pointId, Guid documentId,
            string title, string indexText, float[] vector, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteByDocumentFromAllAsync(Guid documentId, CancellationToken ct = default) => Task.CompletedTask;

        public Task PublishCompletedAsync(Guid documentId, int chunkCount, DateTimeOffset completedAt,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
