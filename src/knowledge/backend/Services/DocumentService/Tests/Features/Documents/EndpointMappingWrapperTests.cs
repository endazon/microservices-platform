using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.Documents;
using DocumentService.Features.PrivateNotes;

namespace DocumentService.Tests.Features.Documents;

// FR-06, FR-09, FR-19, SC-03, SC-09, SC-19, IADR-0153 決定 2 / IADR-0406 決定 1・2:
// **生成マッパの外に残した「導出の指示」**を固定する。
//
// 🔴 **これらは写像ではない。** 辞書引き（タグ ID → 表示名）と縮退（`?? string.Empty` / `?? 0`）は
// `[Mapper]` へ持ち込めない・持ち込んではならないものであり、それを今持っている端
// （登録表 `<集約>Endpoints.cs`）に残った。**ラッパは 1 式で、列の詰め替えを含まない。**
// 列の詰め替え側は `DocumentMapperTests` / `PrivateNoteMapperTests` が見る。
[Trait("TestKind", "Unit")]
public class EndpointMappingWrapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    // 🔴 タグは**識別子で持ち、表示名で出す**（IADR-0153 決定 2）。
    // 辞書に無い ID は落ちる（`TagResolver.ToNames` の既存の振る舞い）。
    [Fact]
    public void ToDto_ResolvesTagIdsToNames()
    {
        var known = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        var d = Document.Create("報告", null, null, tags: [known, unknown]);

        var dto = DocumentEndpoints.ToDto(d, new Dictionary<Guid, string> { [known] = "営業" });

        dto.Tags.Should().Equal("営業");
        dto.Tags.Should().NotContain(known.ToString());
        dto.Tags.Should().NotContain(unknown.ToString());
    }

    // 版も同じ辞書で解決する。**過去版も現在の表示名で出る**（IADR-0153 決定 4）。
    [Fact]
    public void ToVersionDto_ResolvesTagIdsToNames()
    {
        var known = Guid.NewGuid();
        var d = Document.Create("報告", null, null, tags: [known]);
        var v = d.Versions[^1];

        var dto = DocumentEndpoints.ToVersionDto(v, new Dictionary<Guid, string> { [known] = "総務" });

        dto.Tags.Should().Equal("総務");
    }

    // 🔴 **文書の複製がまだ届いていない資料は、題も版も持たない。**
    // 縮退（`""` / `0`）は端の判断であり、生成マッパへ持ち込むと
    // `?? throw new ArgumentNullException` に化ける（本 PR で実測）。
    [Fact]
    public void PrivateNoteToDto_WithoutDocument_DefaultsTitleAndVersion()
    {
        var n = PrivateNote.Create(Guid.NewGuid(), "alice", "研究/メモ.md", 10, "h", Now);

        var dto = PrivateNoteEndpoints.ToDto(n, doc: null);

        dto.Title.Should().BeEmpty();
        dto.Version.Should().Be(0);
        dto.Id.Should().Be(n.DocumentId);
    }

    // 陽性対照: 複製が在れば題と版はそちらから入る（縮退は「無いときだけ」効く）。
    [Fact]
    public void PrivateNoteToDto_WithDocument_TakesTitleAndVersionFromIt()
    {
        var n = PrivateNote.Create(Guid.NewGuid(), "alice", "研究/メモ.md", 10, "h", Now);
        var doc = Document.Create("研究メモ", null, null);

        var dto = PrivateNoteEndpoints.ToDto(n, doc);

        dto.Title.Should().Be("研究メモ");
        dto.Version.Should().Be(doc.Version);
    }
}
