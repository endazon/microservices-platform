using AwesomeAssertions;
using IngestionService.Domain.Ports;
using IngestionService.Domain;

namespace IngestionService.Tests;

// FR-02: チャンク化（見出し分割・overlap）テスト
public class MarkdownChunkingServiceTests
{
    private readonly MarkdownChunkingService _sut = new();

    // T-08: 見出しごとに分割される
    [Fact]
    public void Chunk_ShouldSplitByHeadings()
    {
        var md = "# 見出し1\n\n本文1\n\n## 見出し2\n\n本文2\n\n### 見出し3\n\n本文3";

        var chunks = _sut.Chunk(md);

        chunks.Should().HaveCount(3);
        chunks[0].Should().Contain("本文1");
        chunks[2].Should().Contain("本文3");
    }

    [Fact]
    public void Chunk_ShouldReturnEmpty_ForBlankInput()
    {
        _sut.Chunk("   ").Should().BeEmpty();
        _sut.Chunk("").Should().BeEmpty();
    }

    // T-07: 長いセクションは overlap を伴って複数チャンクへ分割される
    [Fact]
    public void Chunk_ShouldOverlapAdjacentChunks_ForLongSection()
    {
        // 1 セクションに多数の文。小さな maxTokens で必ず分割させる。
        var sentences = Enumerable.Range(0, 40).Select(i => $"これは文番号{i}です").ToArray();
        var md = "# 長いセクション\n\n" + string.Join("。", sentences) + "。";

        var chunks = _sut.Chunk(md, maxTokens: 20, overlap: 5);

        chunks.Count.Should().BeGreaterThan(1);
        // overlap により、隣接チャンクは共有するテキスト断片を持つ
        var sharesContext = false;
        for (var i = 0; i < chunks.Count - 1; i++)
        {
            var tailWords = chunks[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var lastWord = tailWords[^1];
            if (chunks[i + 1].Contains(lastWord)) { sharesContext = true; break; }
        }
        sharesContext.Should().BeTrue("overlap 指定時は隣接チャンクが文脈を共有するはず");
    }

    [Fact]
    public void Chunk_ShouldNotOverlap_WhenOverlapIsZero()
    {
        var sentences = Enumerable.Range(0, 40).Select(i => $"文{i}").ToArray();
        var md = "# S\n\n" + string.Join("。", sentences) + "。";

        var chunks = _sut.Chunk(md, maxTokens: 10, overlap: 0);

        chunks.Count.Should().BeGreaterThan(1);
    }
}
