using AwesomeAssertions;
using IngestionService.Domain.Ports;
using IngestionService.Domain;

namespace IngestionService.Tests.Domain;

// FR-02: 冪等な再取り込みのための決定的チャンク ID テスト
public class ChunkIdTests
{
    // T-04: 同一 documentId + chunkIndex は常に同じ ID
    [Fact]
    public void Derive_ShouldBeDeterministic()
    {
        var doc = Guid.Parse("22222222-2222-2222-2222-222222222222");

        ChunkId.Derive(doc, 0).Should().Be(ChunkId.Derive(doc, 0));
        ChunkId.Derive(doc, 3).Should().Be(ChunkId.Derive(doc, 3));
    }

    [Fact]
    public void Derive_ShouldDifferByIndex()
    {
        var doc = Guid.NewGuid();
        ChunkId.Derive(doc, 0).Should().NotBe(ChunkId.Derive(doc, 1));
    }

    [Fact]
    public void Derive_ShouldDifferByDocument()
    {
        ChunkId.Derive(Guid.NewGuid(), 0).Should().NotBe(ChunkId.Derive(Guid.NewGuid(), 0));
    }

    // FR-02, FR-03, ADR-0070 決定 4, #1193, [[IADR-0354]] 決定 1:
    // メタデータ点（本文なしの文書の 1 点）の ID は決定的で、**どの本文チャンクとも衝突しない**。
    [Fact]
    public void DeriveMetadata_ShouldBeDeterministicAndDistinctFromEveryChunk()
    {
        var doc = Guid.Parse("33333333-3333-3333-3333-333333333333");

        ChunkId.DeriveMetadata(doc).Should().Be(ChunkId.DeriveMetadata(doc));

        // 本文チャンクの索引は 0 以上しか取らない。先頭 512 件と衝突しないことを見る。
        var metadata = ChunkId.DeriveMetadata(doc);
        for (var i = 0; i < 512; i++)
            ChunkId.Derive(doc, i).Should().NotBe(metadata);
    }
}
