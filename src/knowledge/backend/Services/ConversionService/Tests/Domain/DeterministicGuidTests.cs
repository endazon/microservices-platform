using ConversionService.Domain.Ports;
using ConversionService.Domain;
using AwesomeAssertions;

namespace ConversionService.Tests.Domain;

// FR-12, ADR-0012: 冪等な DocumentId 生成（同一原本の再変換で同一 ID）の単体テスト。
public class DeterministicGuidTests
{
    [Fact]
    public void Same_source_and_path_yield_same_guid()
    {
        var sourceId = Guid.NewGuid();
        var a = DeterministicGuid.ForDocument(sourceId, "/docs/a.docx");
        var b = DeterministicGuid.ForDocument(sourceId, "/docs/a.docx");
        a.Should().Be(b);
        a.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void Different_path_yields_different_guid()
    {
        var sourceId = Guid.NewGuid();
        DeterministicGuid.ForDocument(sourceId, "/docs/a.docx")
            .Should().NotBe(DeterministicGuid.ForDocument(sourceId, "/docs/b.docx"));
    }

    [Fact]
    public void Different_source_yields_different_guid()
    {
        DeterministicGuid.ForDocument(Guid.NewGuid(), "/docs/a.docx")
            .Should().NotBe(DeterministicGuid.ForDocument(Guid.NewGuid(), "/docs/a.docx"));
    }
}
