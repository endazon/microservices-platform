using System.Diagnostics;
using AwesomeAssertions;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Messaging;
using RetrievalService.Domain;
using RetrievalService.Domain.Ports;
using RetrievalService.Features.Search.RemoveDeleted;
using RetrievalService.Infrastructure.ExternalServices;
using RetrievalService.Tests.Features.Search.Hybrid;
using Wolverine.Runtime.Handlers;

namespace RetrievalService.Tests.Features.Search.RemoveDeleted;

// FR-06, FR-19, ADR-0027, ADR-0057 (#1640): 索引からの削除の受け口の、Qdrant 1 回ごとの期限。
//
// 🔴 **縮めた受け口の ct の下で測る。** 受け口の ct は Wolverine の実行期限（本受け口は既定 60 秒）を含む。ここでは 30 秒の CTS で模し（呼び出しごとの 1 秒と十分に離し、負荷下の揺らぎで比が崩れないようにする）、
// Qdrant 1 回の期限（1 秒）が**先に**立って時間切れ（`ConsumerTimeoutException`）になること —— 受け口の ct は立っていないこと —— を見る。
// 期限を外す変異では、止まった Qdrant は受け口の ct で取り消し（`OperationCanceledException`）として落ち、赤になる。
[Trait("TestKind", "Unit")]
public class DocumentDeletedTimeoutTests
{
    private static readonly TimeSpan HandlerBudget = TimeSpan.FromSeconds(30);
    private static readonly DocumentDeletedTimeouts Scaled = new(TimeSpan.FromSeconds(1), CollectionCount: 2);

    private static DocumentDeletedConsumer Build(IVectorStore primary, IVectorStore fused, DocumentDeletedTimeouts timeouts)
        => new(primary, new FusedCollections([new FusedCollection("extra", fused, new FixedEmbedding([]))]),
            ConsumerTimeoutsForTests.Calls(), timeouts, NullLogger<DocumentDeletedConsumer>.Instance);

    // 止まった Qdrant（主・追加のどちらでも）は、受け口の ct が立つより前に時間切れとして投げる（再試行・デッドレターへ）。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 止まった_Qdrant_は受け口の期限より前に時間切れとして投げる(bool primaryHangs)
    {
        var consumer = Build(
            primaryHangs ? new HangingDeleteStore() : new InMemoryVectorStore(),
            primaryHangs ? new InMemoryVectorStore() : new HangingDeleteStore(),
            Scaled);
        using var handler = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        handler.CancelAfter(HandlerBudget);
        var started = Stopwatch.GetTimestamp();

        var act = () => consumer.Handle(new DocumentDeleted(Guid.NewGuid(), DateTimeOffset.UtcNow), handler.Token);

        var thrown = (await act.Should().ThrowAsync<ConsumerTimeoutException>()).Which;
        handler.IsCancellationRequested.Should().BeFalse("Qdrant 1 回の期限が受け口の ct より先に立つ");
        Stopwatch.GetElapsedTime(started).Should().BeLessThan(HandlerBudget);
        thrown.Step.Should().Be(DocumentDeletedConsumer.StepName);
        thrown.Target.Should().Be(DocumentDeletedTimeouts.VectorStoreTarget);
    }

    // 対照: 呼び出し元の取り消しは時間切れに化けず、取り消しのまま外へ出る。
    [Fact]
    public async Task 呼び出し元の取り消しは取り消しのまま外へ出る()
    {
        var consumer = Build(new HangingDeleteStore(), new InMemoryVectorStore(),
            Scaled with { VectorStore = TimeSpan.FromSeconds(30) });
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(TimeSpan.FromMilliseconds(200));

        var act = () => consumer.Handle(new DocumentDeleted(Guid.NewGuid(), DateTimeOffset.UtcNow), caller.Token);

        (await act.Should().ThrowAsync<OperationCanceledException>()).Which
            .Should().NotBeAssignableTo<TimeoutException>();
    }

    private static IConfiguration Config(string? seconds) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [DocumentDeletedTimeouts.VectorStoreKey] = seconds })
        .Build();

    [Fact]
    public void 構成が無ければ既定の期限になる()
    {
        // #1746: 本番の配線は語彙索引の 1 本を追加の本数に数えて渡す（`Program.cs`）。既定は追加 0 本 ＋ 語彙索引 1 本。
        var timeouts = DocumentDeletedTimeouts.From(Config(null), fusedCollectionCount: 1);

        timeouts.Should().Be(DocumentDeletedTimeouts.Default);
        timeouts.VectorStore.Should().Be(TimeSpan.FromSeconds(10));
    }

    // 「(主 ＋ 追加) × 期限」が Wolverine の既定 60 秒以上なら起動を止める（等しいときも止める）。
    [Theory]
    [InlineData(null, 5)]
    [InlineData("20", 2)]
    public void 削除の最悪の所要時間が既定の実行期限に収まらなければ起動を止める(string? seconds, int fused)
    {
        var act = () => DocumentDeletedTimeouts.From(Config(seconds), fused);

        act.Should().Throw<InvalidOperationException>().WithMessage("*retrieval-delete*");
    }

    [Fact]
    public void 追加が_4_本までは既定の期限で起動する()
    {
        DocumentDeletedTimeouts.From(Config(null), 4).CollectionCount.Should().Be(5);
    }

    // Qdrant の削除だけが止まる（ct が立つまで返らない）ストア。他の口は使わない。
    private sealed class HangingDeleteStore : IVectorStore
    {
        public async Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct = default)
            => await Task.Delay(Timeout.Infinite, ct);

        public Task<List<SearchResultDto>> SearchAsync(float[] queryVector, int topK, ScopeFilter? filters,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<List<SearchResultDto>> SearchWithinDocumentsAsync(float[] queryVector, int topK,
            IReadOnlyCollection<Guid> documentIds, ScopeFilter? filters, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<List<SearchResultDto>> KeywordSearchAsync(string query, int topK, ScopeFilter? filters,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<List<string>> ListAttributeValuesAsync(string payloadKey, ScopeFilter? filters,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task UpsertAsync(ChunkPayload chunk, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

// 本番の Program.cs の配線: 上限を DI に置き、実行期限は Wolverine の既定のまま（方針を入れない）。
[Trait("TestKind", "Integration")]
public class DocumentDeletedTimeoutWiringTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public void 本番の配線は削除の期限を既定値で張り実行期限は既定のまま()
    {
        var services = factory.Services;

        services.GetRequiredService<DocumentDeletedTimeouts>().Should().Be(DocumentDeletedTimeouts.Default);
        services.GetRequiredService<ConsumerCallTimeouts>().Should().NotBeNull();
        services.GetRequiredService<HandlerGraph>().Chains
            .Should().ContainSingle(c => c.MessageType == typeof(DocumentDeleted))
            .Which.ExecutionTimeoutInSeconds.Should().BeNull();
    }
}
