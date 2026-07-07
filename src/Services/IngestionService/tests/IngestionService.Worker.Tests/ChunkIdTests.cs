using FluentAssertions;
using IngestionService.Worker.Foundation.Ports;
using IngestionService.Worker.Foundation.Domain;

namespace IngestionService.Worker.Tests;

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
}
