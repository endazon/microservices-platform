using System.Text.Json;
using AwesomeAssertions;
using Knowledge.Contracts.Events;
using Xunit;

namespace Knowledge.Contracts.Tests;

// 🔴 FR-02, FR-19, IADR-0637 (#2150 / #2251) 段 1a: 発行の連番 `Sequence` は**末尾・既定値つき**の追加である
// （IADR-0122 決定 2）。連番を持たない旧い電文（outbox に残った発行・旧い送り手）は null として読め、
// 連番を読まない受け手（段 2 まで）は連番の有無で解釈を変えない。直列化は Wolverine の既定（System.Text.Json）に合わせる。
public class DocumentEventSequenceCompatibilityTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 連番を持たない旧いDocumentUpdatedは連番なしとして読める()
    {
        var legacy = $$"""
            {"DocumentId":"{{Guid.Empty}}","Title":"t","Status":"Draft","MarkdownUri":null,
             "Attributes":{},"Tags":["a"],"UpdatedAt":"2026-10-26T00:00:00+00:00"}
            """;

        var read = JsonSerializer.Deserialize<DocumentUpdated>(legacy)!;

        read.Sequence.Should().BeNull("旧い電文は連番を持たない（受け手は UpdatedAt の比較へ倒す。段 2）");
        read.Tags.Should().Equal("a");
        read.UpdatedAt.Should().Be(At);
    }

    [Fact]
    public void 連番を持たない旧いDocumentDeletedは連番なしとして読める()
    {
        var legacy = $$"""{"DocumentId":"{{Guid.Empty}}","DeletedAt":"2026-10-26T00:00:00+00:00"}""";

        var read = JsonSerializer.Deserialize<DocumentDeleted>(legacy)!;

        read.Sequence.Should().BeNull();
        read.DeletedAt.Should().Be(At);
    }

    [Fact]
    public void 連番は往復で保たれる()
    {
        var updated = new DocumentUpdated(Guid.NewGuid(), "t", "Published", null, [], [], At, Sequence: 7);
        var deleted = new DocumentDeleted(Guid.NewGuid(), At, Sequence: 8);

        JsonSerializer.Deserialize<DocumentUpdated>(JsonSerializer.Serialize(updated))!.Sequence.Should().Be(7);
        JsonSerializer.Deserialize<DocumentDeleted>(JsonSerializer.Serialize(deleted))!.Sequence.Should().Be(8);
    }

    // 連番を読まない受け手に対して、連番以外の内容は同じ値である（連番の有無で等価の比較が変わる箇所は連番だけ）。
    [Fact]
    public void 連番の有無は連番以外の内容を変えない()
    {
        var without = new DocumentUpdated(Guid.Empty, "t", "Draft", null, [], [], At);
        var with = without with { Sequence = 3 };

        (with with { Sequence = null }).Should().BeEquivalentTo(without);
    }
}
