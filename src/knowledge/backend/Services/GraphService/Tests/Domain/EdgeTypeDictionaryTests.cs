using AwesomeAssertions;
using GraphService.Domain;
using GraphService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GraphService.Tests.Domain;

// FR-17, SC-09, ADR-0033 決定 3・9, IADR-0242 決定 7: 辺の型辞書の性質を固定する。
//
// CRUD の API は #910 が足す。ここで固定するのは**モデルの性質**である ——
// 改名が既存の辺を書き換えないこと、初期値集合が入ること、seed が既存を壊さないこと。
public class EdgeTypeDictionaryTests
{
    private static GraphDbContext NewDb()
        => new(new DbContextOptionsBuilder<GraphDbContext>()
            .UseInMemoryDatabase($"EdgeType_{Guid.NewGuid()}")
            .Options);

    // 🔴 ADR-0033 決定 9: **改名は許し、既存の辺は新しい名前へ追随する。**
    // 辺が識別子を参照しているため、改名で辺の行は 1 行も変わらない。
    [Fact]
    public async Task Rename_does_not_touch_any_edge_row()
    {
        await using var db = NewDb();
        var type = EdgeType.Create("related", EdgeTypeLayer.Core, isSymmetric: true, isSeed: true);
        db.EdgeTypes.Add(type);
        var edge = Edge.Create(Guid.NewGuid(), Guid.NewGuid(), type.Id, true, EdgeProvenance.Auto);
        db.Edges.Add(edge);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var before = await db.Edges.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

        type.Rename("relates-to");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var after = await db.Edges.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

        after.Id.Should().Be(before.Id);
        after.EdgeTypeId.Should().Be(before.EdgeTypeId);
        after.SourceDocumentId.Should().Be(before.SourceDocumentId);
        after.TargetDocumentId.Should().Be(before.TargetDocumentId);
        after.Provenance.Should().Be(before.Provenance);
        after.UpdatedAt.Should().Be(before.UpdatedAt,
            "改名は辺の内容変更ではないため、辺の更新時刻すら動いてはならない");

        // 追随していること（辺は表示名を複写していない）。
        var resolved = await db.EdgeTypes.AsNoTracking().SingleAsync(t => t.Id == after.EdgeTypeId, TestContext.Current.CancellationToken);
        resolved.Name.Should().Be("relates-to");
    }

    // SC-09: 名前は正規化して比較する（前後の空白だけが違う 2 つを別物にしない）。
    [Fact]
    public void Name_is_normalized_on_create_and_rename()
    {
        var t = EdgeType.Create("  related  ", EdgeTypeLayer.Core, true);
        t.Name.Should().Be("related");

        t.Rename("  cites ");
        t.Name.Should().Be("cites");
    }

    // ADR-0033 決定 3: 初期値集合（中核 5 種 ＋ 推奨 4 種）が入る。
    [Fact]
    public async Task Seed_inserts_core_and_recommended_types()
    {
        await using var db = NewDb();

        await EdgeTypeSeed.EnsureSeededAsync(db, TestContext.Current.CancellationToken);

        var all = await db.EdgeTypes.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        all.Should().HaveCount(EdgeTypeSeed.Count);
        all.Count(t => t.Layer == EdgeTypeLayer.Core).Should().Be(5);
        all.Count(t => t.Layer == EdgeTypeLayer.Recommended).Should().Be(4);

        // 自動抽出の既定型は related であり、唯一の対称型である。
        var related = all.Single(t => t.Name == EdgeTypeSeed.DefaultTypeName);
        related.IsSymmetric.Should().BeTrue();
        all.Count(t => t.IsSymmetric).Should().Be(1);
    }

    [Fact]
    public async Task Seed_is_idempotent()
    {
        await using var db = NewDb();

        await EdgeTypeSeed.EnsureSeededAsync(db, TestContext.Current.CancellationToken);
        await EdgeTypeSeed.EnsureSeededAsync(db, TestContext.Current.CancellationToken);

        (await db.EdgeTypes.CountAsync(TestContext.Current.CancellationToken)).Should().Be(EdgeTypeSeed.Count);
    }

    // 🔴 ADR-0033 決定 9: seed が**改名を巻き戻さない**。
    // ここが壊れると、SC-09 で改名した型が起動のたびに元の名前へ戻る（しかも重複して増える）。
    [Fact]
    public async Task Seed_does_not_resurrect_renamed_types()
    {
        await using var db = NewDb();
        await EdgeTypeSeed.EnsureSeededAsync(db, TestContext.Current.CancellationToken);

        var cites = await db.EdgeTypes.SingleAsync(t => t.Name == "cites", TestContext.Current.CancellationToken);
        var id = cites.Id;
        cites.Rename("references");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await EdgeTypeSeed.EnsureSeededAsync(db, TestContext.Current.CancellationToken);

        var renamed = await db.EdgeTypes.AsNoTracking().SingleAsync(t => t.Id == id, TestContext.Current.CancellationToken);
        renamed.Name.Should().Be("references");
        (await db.EdgeTypes.CountAsync(t => t.Name == "cites", TestContext.Current.CancellationToken)).Should()
            .Be(1, "改名で空いた名前は seed が埋め直すが、改名した型を戻してはならない");
    }

    // IADR-0242 決定 9: 対称型は (min, max) へ正規化して 1 行にする。
    [Fact]
    public void Symmetric_edges_are_normalized_by_document_id_order()
    {
        var a = new Guid("00000000-0000-0000-0000-00000000000a");
        var b = new Guid("00000000-0000-0000-0000-00000000000b");
        var typeId = Guid.NewGuid();

        var forward = Edge.Create(a, b, typeId, isSymmetric: true, EdgeProvenance.Auto);
        var backward = Edge.Create(b, a, typeId, isSymmetric: true, EdgeProvenance.Auto);

        forward.SourceDocumentId.Should().Be(backward.SourceDocumentId);
        forward.TargetDocumentId.Should().Be(backward.TargetDocumentId);
    }

    // 方向型は正規化しない（向きが意味そのものであるため）。
    [Fact]
    public void Directed_edges_keep_their_direction()
    {
        var a = new Guid("00000000-0000-0000-0000-00000000000a");
        var b = new Guid("00000000-0000-0000-0000-00000000000b");
        var typeId = Guid.NewGuid();

        var edge = Edge.Create(b, a, typeId, isSymmetric: false, EdgeProvenance.Auto);

        edge.SourceDocumentId.Should().Be(b);
        edge.TargetDocumentId.Should().Be(a);
    }

    // 対称型の入れ替えではアンカーも組で入れ替わる（片方だけ据え置くと意味が化ける）。
    [Fact]
    public void Symmetric_normalization_swaps_anchors_together()
    {
        var a = new Guid("00000000-0000-0000-0000-00000000000a");
        var b = new Guid("00000000-0000-0000-0000-00000000000b");

        var edge = Edge.Create(b, a, Guid.NewGuid(), isSymmetric: true, EdgeProvenance.Auto,
            sourceAnchor: "from-b", targetAnchor: "to-a");

        edge.SourceDocumentId.Should().Be(a);
        edge.SourceAnchor.Should().Be("to-a");
        edge.TargetAnchor.Should().Be("from-b");
    }

    // ADR-0033 決定 5: アンカー未指定は空文字（NULL にしない。一意制約が効かなくなるため）。
    [Fact]
    public void Unspecified_anchors_are_empty_strings_not_null()
    {
        var edge = Edge.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), false, EdgeProvenance.User);

        edge.SourceAnchor.Should().BeEmpty();
        edge.TargetAnchor.Should().BeEmpty();
    }
}
