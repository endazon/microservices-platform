using AwesomeAssertions;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.Logging.Abstractions;
using RetrievalService.Infrastructure.ExternalServices;
using RetrievalService.Features.Search.RemoveDeleted;
using RetrievalService.Domain.Ports;

namespace RetrievalService.Tests.Features.Search.RemoveDeleted;

// FR-06, FR-19, ADR-0057 決定 1 (#1016): 削除の伝播 —— 削除された文書のチャンクが検索索引から消える。
//
// 🔴 **否定形テストには必ず陽性対照を対で置く**（GraphTraversalTests と同じ作法）。
// 「削除後に出ない」だけでは、そもそも索引されていなくても緑になる。
[Trait("TestKind", "Unit")]
public class DocumentDeletedConsumerTests
{
    private static readonly Guid DocA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid DocB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static ChunkPayload Chunk(Guid docId, string text) => new(
        Guid.NewGuid(), docId, $"doc-{docId:N}", text, [0.1f, 0.2f],
        $"storage://bucket/{docId:N}.md",
        new Dictionary<string, string> { ["confidentiality"] = "internal" },
        ["ops"], DateTimeOffset.UtcNow);

    [Fact]
    public async Task 削除された文書のチャンクは検索に出ない_他文書は残る()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryVectorStore();
        await store.UpsertAsync(Chunk(DocA, "削除対象の規程本文"), ct);
        await store.UpsertAsync(Chunk(DocA, "削除対象の規程別紙"), ct);
        await store.UpsertAsync(Chunk(DocB, "残るべき規程本文"), ct);

        // 陽性対照: 削除前は両文書とも検索に出る（これが無いと下の否定形は何も測っていない）。
        var before = await store.KeywordSearchAsync("規程", 10, null, ct);
        before.Select(r => r.DocumentId).Distinct().Should().BeEquivalentTo([DocA, DocB]);

        var consumer = new DocumentDeletedConsumer(store, NullLogger<DocumentDeletedConsumer>.Instance);
        await consumer.Handle(new DocumentDeleted(DocA, DateTimeOffset.UtcNow), ct);

        // 否定形: 削除文書のチャンクは 1 件も出ない。
        var after = await store.KeywordSearchAsync("規程", 10, null, ct);
        after.Should().NotContain(r => r.DocumentId == DocA);
        // 対照: 無関係の文書は消えない（全消しになっていない）。
        after.Should().Contain(r => r.DocumentId == DocB);
    }

    [Fact]
    public async Task 該当チャンクが無くても成功する_再配信に冪等()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryVectorStore();
        var consumer = new DocumentDeletedConsumer(store, NullLogger<DocumentDeletedConsumer>.Instance);

        // 未索引の文書 ID・二重配信のいずれも例外にしない（例外なら本テスト自体が失敗する）。
        await consumer.Handle(new DocumentDeleted(DocA, DateTimeOffset.UtcNow), ct);
        await consumer.Handle(new DocumentDeleted(DocA, DateTimeOffset.UtcNow), ct);
    }
}
