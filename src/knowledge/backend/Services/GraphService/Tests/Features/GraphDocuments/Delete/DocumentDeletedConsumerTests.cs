using AwesomeAssertions;
using GraphService.Features.GraphDocuments.Delete;
using GraphService.Domain;
using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphService.Tests.Features.GraphDocuments.Delete;

// FR-17, FR-06, FR-19, ADR-0057 (#1016): 削除の伝播 —— 削除された文書の痕跡
// （ノード・両端いずれかが当該文書の辺・AI 提案）がグラフから消える。
//
// 🔴 **否定形テストには必ず陽性対照を対で置く**（GraphTraversalTests と同じ作法）——
// 「削除後に無い」だけでは、そもそもフィクスチャが入っていなくても緑になる。
[Trait("TestKind", "Unit")]
public class DocumentDeletedConsumerTests
{
    private static readonly Guid DocA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid DocB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid DocC = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private static GraphDbContext NewDb() => new(
        new DbContextOptionsBuilder<GraphDbContext>()
            .UseInMemoryDatabase($"del_{Guid.NewGuid():N}").Options);

    private static GraphDocument Node(Guid id) => GraphDocument.Create(
        id, $"doc-{id:N}",
        new Dictionary<string, string> { ["confidentiality"] = "internal" },
        null, DateTimeOffset.UtcNow);

    private static async Task<(GraphDbContext Db, EdgeType Type)> SeedAsync(CancellationToken ct)
    {
        var db = NewDb();
        var type = EdgeType.Create("cites", EdgeTypeLayer.Core, isSymmetric: false, isSeed: true);
        db.EdgeTypes.Add(type);
        db.Documents.AddRange(Node(DocA), Node(DocB), Node(DocC));
        // 当該文書が起点の辺 / 対象の辺（provenance を問わず消える）・無関係の辺（残る）。
        db.Edges.AddRange(
            Edge.Create(DocA, DocB, type.Id, isSymmetric: false, EdgeProvenance.User),
            Edge.Create(DocC, DocA, type.Id, isSymmetric: false, EdgeProvenance.Auto),
            Edge.Create(DocB, DocC, type.Id, isSymmetric: false, EdgeProvenance.Auto));
        // 当該文書起点の pending・当該文書対象の rejected・無関係の pending。
        var pendingFromA = AiSuggestion.CreateLink(DocA, DocC, type.Id, "根拠A", DateTimeOffset.UtcNow);
        var rejectedToA = AiSuggestion.CreateLink(DocB, DocA, type.Id, "根拠B", DateTimeOffset.UtcNow);
        rejectedToA.TryReject("s1", "t1", DateTimeOffset.UtcNow).Should().BeTrue();
        var unrelated = AiSuggestion.CreateLink(DocB, DocC, type.Id, "根拠C", DateTimeOffset.UtcNow);
        db.AiSuggestions.AddRange(pendingFromA, rejectedToA, unrelated);
        await db.SaveChangesAsync(ct);
        return (db, type);
    }

    [Fact]
    public async Task 削除された文書のノードと辺とAI提案が消える_無関係は残る()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, _) = await SeedAsync(ct);

        // 陽性対照: 削除前は全部そろっている。
        db.Documents.Count().Should().Be(3);
        db.Edges.Count().Should().Be(3);
        db.AiSuggestions.Count().Should().Be(3);

        var consumer = new DocumentDeletedConsumer(db, NullLogger<DocumentDeletedConsumer>.Instance);
        await consumer.Handle(new DocumentDeleted(DocA, DateTimeOffset.UtcNow), ct);

        // 否定形: 当該文書の痕跡が 1 件も残らない。
        db.Documents.Any(d => d.DocumentId == DocA).Should().BeFalse("ノード（属性複製）が消える");
        db.Edges.Any(e => e.SourceDocumentId == DocA || e.TargetDocumentId == DocA)
            .Should().BeFalse("両端いずれかが当該文書の辺は provenance を問わず消える");
        db.AiSuggestions.Any(s => s.SourceDocumentId == DocA || s.TargetDocumentId == DocA)
            .Should().BeFalse("pending・rejected を含む全状態の提案が消える");

        // 対照: 無関係のノード・辺・提案は消えない（全消しになっていない）。
        db.Documents.Select(d => d.DocumentId).Should().BeEquivalentTo([DocB, DocC]);
        db.Edges.Count().Should().Be(1);
        db.Edges.Single().SourceDocumentId.Should().Be(DocB);
        db.AiSuggestions.Count().Should().Be(1);
        db.AiSuggestions.Single().TargetDocumentId.Should().Be(DocC);
    }

    [Fact]
    public async Task 該当が無くても成功する_再配信に冪等()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, _) = await SeedAsync(ct);
        var consumer = new DocumentDeletedConsumer(db, NullLogger<DocumentDeletedConsumer>.Instance);

        await consumer.Handle(new DocumentDeleted(DocA, DateTimeOffset.UtcNow), ct);
        // 二重配信・未知 ID とも例外にしない（例外なら本テスト自体が失敗する）。
        await consumer.Handle(new DocumentDeleted(DocA, DateTimeOffset.UtcNow), ct);
        await consumer.Handle(new DocumentDeleted(Guid.NewGuid(), DateTimeOffset.UtcNow), ct);

        db.Documents.Select(d => d.DocumentId).Should().BeEquivalentTo([DocB, DocC]);
    }
}
