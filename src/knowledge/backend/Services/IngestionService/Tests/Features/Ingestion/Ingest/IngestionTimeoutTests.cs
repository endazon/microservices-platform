using System.Diagnostics;
using AwesomeAssertions;
using IngestionService.Domain;
using IngestionService.Domain.Ports;
using IngestionService.Features.Ingestion.Ingest;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Platform.Shared.Infrastructure.Foundation.Messaging;
using Wolverine.Runtime.Handlers;

namespace IngestionService.Tests.Features.Ingestion.Ingest;

// FR-02 テスト仕様 T-18〜T-21 (#1640) —— UC-04, ADR-0013, ADR-0016, ADR-0027:
// 取り込みの受け口の時間の上限（呼び出しごとの期限・埋め込みの総枠・受け口の実行期限）。
//
// 🔴 **縮めた受け口の ct の下で測る。** 受け口の ct は Wolverine の実行期限を含む（本番の既定 720 秒・方針なしなら 60 秒）。
// ここではそれを 4 秒の CTS で模し、呼び出しごとの期限（1 秒）が**先に**立って時間切れ（`ConsumerTimeoutException`）に
// なること —— 受け口の ct は立っていないこと —— を見る。呼び出しごとの期限を外す変異では、止まった依存先は
// 受け口の ct で取り消し（`OperationCanceledException`）として落ち、これらの試験が赤になる。
[Trait("TestKind", "Unit")]
public class IngestionTimeoutTests
{
    private static readonly TimeSpan HandlerBudget = TimeSpan.FromSeconds(4);

    // 縮尺: 呼び出しごと 1 秒、総枠は十分長く（期限の試験で総枠が先に立たないように）。
    private static readonly IngestionTimeouts Scaled = new(
        ContentRead: TimeSpan.FromSeconds(1),
        Embedding: TimeSpan.FromSeconds(1),
        VectorStore: TimeSpan.FromSeconds(1),
        EmbeddingBudget: TimeSpan.FromSeconds(600),
        Handler: HandlerBudget,
        CollectionCount: 1);

    public enum Hung { Content, Embedding, Delete, Upsert, MetadataUpsert }

    private static DocumentUpdated Event(string? markdownUri = "storage://bucket/doc.md") => new(
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        "時間切れの試験",
        "normalized",
        markdownUri,
        new Dictionary<string, string> { ["confidentiality"] = "internal" },
        ["ops"],
        DateTimeOffset.UtcNow);

    private static DocumentUpdatedConsumer Build(
        HangingPorts ports, IngestionTimeouts timeouts, TimeProvider? clock = null, IEmbeddingService? embed = null)
        => new(
            ports, new MarkdownChunkingService(), embed ?? ports, ports, ports,
            ConsumerTimeoutsForTests.Calls(), timeouts, clock ?? TimeProvider.System,
            NullLogger<DocumentUpdatedConsumer>.Instance);

    // T-18: 止まった依存先（本文・埋め込み・既存チャンクの削除・チャンクの登録・メタデータ点の登録）は、
    // 受け口の ct が立つより前に、その呼び出し先の時間切れとして投げられる（再試行・デッドレターへ）。
    [Theory]
    [InlineData(Hung.Content, IngestionTimeouts.ContentTarget)]
    [InlineData(Hung.Embedding, IngestionTimeouts.EmbeddingTarget)]
    [InlineData(Hung.Delete, IngestionTimeouts.VectorStoreTarget)]
    [InlineData(Hung.Upsert, IngestionTimeouts.VectorStoreTarget)]
    [InlineData(Hung.MetadataUpsert, IngestionTimeouts.VectorStoreTarget)]
    public async Task 止まった依存先は受け口の期限より前に時間切れとして投げる(Hung hung, string target)
    {
        var ports = new HangingPorts(hung, body: hung == Hung.MetadataUpsert ? "" : "# 見出し\n\n本文");
        var consumer = Build(ports, Scaled);
        using var handler = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        handler.CancelAfter(HandlerBudget);
        var started = Stopwatch.GetTimestamp();

        var act = () => consumer.Handle(Event(), handler.Token);

        var thrown = (await act.Should().ThrowAsync<ConsumerTimeoutException>()).Which;
        handler.IsCancellationRequested.Should().BeFalse("呼び出しごとの期限が受け口の ct より先に立つ");
        Stopwatch.GetElapsedTime(started).Should().BeLessThan(HandlerBudget);
        thrown.Step.Should().Be(DocumentUpdatedConsumer.StepName);
        thrown.Target.Should().Be(target);
        ports.Published.Should().BeFalse("時間切れの文書は取り込み完了にしない");
    }

    // T-19（対照）: 呼び出し元の取り消し（停止要求・受け口の実行期限）は時間切れに化けず、取り消しのまま外へ出る。
    [Theory]
    [InlineData(Hung.Content)]
    [InlineData(Hung.Embedding)]
    [InlineData(Hung.Delete)]
    public async Task 呼び出し元の取り消しは取り消しのまま外へ出る(Hung hung)
    {
        var ports = new HangingPorts(hung, body: "# 見出し\n\n本文");
        // 呼び出しごとの期限は長く、呼び出し元が先に取り消す。
        var consumer = Build(ports, Scaled with
        {
            ContentRead = TimeSpan.FromSeconds(30),
            Embedding = TimeSpan.FromSeconds(30),
            VectorStore = TimeSpan.FromSeconds(30),
        });
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(TimeSpan.FromMilliseconds(200));

        var act = () => consumer.Handle(Event(), caller.Token);

        var thrown = (await act.Should().ThrowAsync<OperationCanceledException>()).Which;
        thrown.Should().NotBeAssignableTo<TimeoutException>();
        caller.IsCancellationRequested.Should().BeTrue();
    }

    // T-20: 埋め込みの総枠はチャンクごとに呼び出しの**前**に判定し、使い切ったら残りを呼ばずに時間切れとして投げる。
    // 時計は偽物で、埋め込み 1 回ごとに 1 秒進める（壁時計を待たない）。総枠 3 秒なら 3 回呼んだ後の判定で止まる。
    [Fact]
    public async Task 埋め込みの総枠を使い切ったら残りを呼ばずに時間切れとして投げる()
    {
        var clock = new FakeTimeProvider();
        var ports = new HangingPorts(hung: null, body: ManySections(10));
        var embed = new ClockAdvancingEmbedder(clock, TimeSpan.FromSeconds(1));
        var consumer = Build(ports, Scaled with { EmbeddingBudget = TimeSpan.FromSeconds(3) }, clock, embed);

        var act = () => consumer.Handle(Event(), TestContext.Current.CancellationToken);

        var thrown = (await act.Should().ThrowAsync<ConsumerTimeoutException>()).Which;
        thrown.Target.Should().Be(IngestionTimeouts.EmbeddingBudgetTarget);
        thrown.Message.Should().Contain("3 of 10 chunks");
        embed.Calls.Should().Be(3, "総枠を使い切った後は埋め込みを呼ばない");
        ports.Published.Should().BeFalse();
    }

    // T-20（対照）: 総枠に収まる文書は全チャンクを埋め込み、完了を発行する。
    [Fact]
    public async Task 総枠に収まれば全チャンクを埋め込む()
    {
        var clock = new FakeTimeProvider();
        var ports = new HangingPorts(hung: null, body: ManySections(10));
        var embed = new ClockAdvancingEmbedder(clock, TimeSpan.FromSeconds(1));
        var consumer = Build(ports, Scaled with { EmbeddingBudget = TimeSpan.FromSeconds(11) }, clock, embed);

        await consumer.Handle(Event(), TestContext.Current.CancellationToken);

        embed.Calls.Should().Be(10);
        ports.Upserts.Should().Be(10);
        ports.Published.Should().BeTrue();
    }

    private static string ManySections(int count) =>
        string.Join("\n\n", Enumerable.Range(1, count).Select(i => $"# 節 {i}\n\n本文 {i}"));

    // ── 構成と起動時の検査 ──

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    // T-21: 既定の上限と、それが既定のコレクション数（2）で受け口の実行期限に収まること。
    [Fact]
    public void 構成が無ければ既定の上限になる()
    {
        var timeouts = IngestionTimeouts.From(Config([]), IngestionTimeouts.DefaultCollectionCount);

        timeouts.Should().Be(IngestionTimeouts.Default);
        timeouts.ContentRead.Should().Be(TimeSpan.FromSeconds(20));
        timeouts.Embedding.Should().Be(TimeSpan.FromSeconds(30));
        timeouts.VectorStore.Should().Be(TimeSpan.FromSeconds(10));
        timeouts.EmbeddingBudget.Should().Be(TimeSpan.FromSeconds(600));
        timeouts.Handler.Should().Be(TimeSpan.FromSeconds(720));
        timeouts.DeleteFromAll.Should().Be(TimeSpan.FromSeconds(20), "コレクション 2 本 × Qdrant 10 秒");
    }

    // T-21: 受け口の実行期限が「削除（本数 × Qdrant）＋本文＋総枠＋最後の 1 チャンク」以下なら起動を止める。
    // 既定値では 20＋20＋600＋30＋10＝680 秒。コレクションが 6 本なら 60＋20＋600＋30＋10＝720 秒で、既定の 720 秒と等しく止まる。
    [Theory]
    [InlineData(6, null)]
    [InlineData(2, "680")]
    [InlineData(2, "60")]
    public void 受け口の実行期限に最悪の所要時間が収まらなければ起動を止める(int collections, string? handler)
    {
        var act = () => IngestionTimeouts.From(
            Config(new() { [IngestionTimeouts.HandlerKey] = handler }), collections);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{IngestionTimeouts.HandlerKey}*");
    }

    [Fact]
    public void 総枠を上げるなら受け口の実行期限も上げれば通る()
    {
        var timeouts = IngestionTimeouts.From(Config(new()
        {
            [IngestionTimeouts.EmbeddingBudgetKey] = "1800",
            [IngestionTimeouts.HandlerKey] = "1900",
        }), 2);

        timeouts.EmbeddingBudget.Should().Be(TimeSpan.FromSeconds(1800));
        timeouts.Handler.Should().Be(TimeSpan.FromSeconds(1900));
    }

    // ── 部品 ──

    // どれか 1 つの依存先だけが止まる（ct が立つまで返らない）ポート群。他は即時に成功する。
    private sealed class HangingPorts(Hung? hung, string body)
        : IDocumentContentReader, IEmbeddingService, IIngestionVectorStore, IIngestionCompletedPublisher
    {
        public bool Published { get; private set; }
        public int Upserts { get; private set; }

        private async Task HangIf(Hung which, CancellationToken ct)
        {
            if (hung == which)
                await Task.Delay(Timeout.Infinite, ct);
        }

        public async Task<string> ReadAsync(string markdownUri, string title, CancellationToken ct = default)
        {
            await HangIf(Hung.Content, ct);
            return body;
        }

        public async Task<EmbeddingResult> EmbedAsync(string text, string? confidentiality, CancellationToken ct = default)
        {
            await HangIf(Hung.Embedding, ct);
            return new EmbeddingResult(new float[4], "c", true);
        }

        public Task EnsureCollectionsAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async Task UpsertChunkAsync(string collection, Guid chunkId, Guid documentId, string title,
            string text, int chunkIndex, float[] vector, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null, CancellationToken ct = default)
        {
            await HangIf(Hung.Upsert, ct);
            Upserts++;
        }

        public Task UpsertMetadataPointAsync(string collection, Guid pointId, Guid documentId,
            string title, string indexText, float[] vector, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null, CancellationToken ct = default)
            => HangIf(Hung.MetadataUpsert, ct);

        public Task DeleteByDocumentFromAllAsync(Guid documentId, CancellationToken ct = default)
            => HangIf(Hung.Delete, ct);

        public Task PublishCompletedAsync(Guid documentId, int chunkCount, DateTimeOffset completedAt,
            CancellationToken ct = default)
        {
            Published = true;
            return Task.CompletedTask;
        }
    }

    // 1 回の埋め込みごとに偽の時計を進める（遅いが止まってはいない依存先）。
    private sealed class ClockAdvancingEmbedder(FakeTimeProvider clock, TimeSpan perCall) : IEmbeddingService
    {
        public int Calls { get; private set; }

        public Task<EmbeddingResult> EmbedAsync(string text, string? confidentiality, CancellationToken ct = default)
        {
            Calls++;
            clock.Advance(perCall);
            return Task.FromResult(new EmbeddingResult(new float[4], "c", true));
        }
    }
}

// T-21: 本番の Program.cs の配線 —— 取り込みの受け口の実行期限（720 秒）と上限の値が DI から引けること。
[Trait("TestKind", "Integration")]
public class IngestionTimeoutWiringTests(IntrospectionEndpointTests.Factory factory)
    : IClassFixture<IntrospectionEndpointTests.Factory>
{
    [Fact]
    public void 本番の配線は受け口の実行期限と上限を既定値で張る()
    {
        var services = factory.Services;

        services.GetRequiredService<HandlerGraph>().Chains
            .Should().ContainSingle(c => c.MessageType == typeof(DocumentUpdated))
            .Which.ExecutionTimeoutInSeconds.Should().Be(IngestionTimeouts.DefaultHandlerSeconds);

        // appsettings.json の `Embedding:Collections` は 2 本 → 既定と一致する。
        services.GetRequiredService<IngestionTimeouts>().Should().Be(IngestionTimeouts.Default);
        services.GetRequiredService<ConsumerCallTimeouts>().Should().NotBeNull();
    }
}
