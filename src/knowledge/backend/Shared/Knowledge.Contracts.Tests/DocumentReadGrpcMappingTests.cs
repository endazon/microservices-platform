using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Grpc;
using Xunit;

namespace Knowledge.Contracts.Tests;

// FR-06, UC-03, SC-03, NFR-09, ADR-0029, ADR-0070 決定 3, ADR-0075, [[IADR-0290]], [[IADR-0379]],
// [[IADR-0388]] 決定 2, [[IADR-0397]] 決定 5, [[IADR-0400]] 決定 4, [[IADR-0402]] (#1255):
// 文書読み取りの DTO ↔ proto の写像。**proto3 に無い「null」と、既定値が逆向きの真偽値**を固定する。
//
// ここは呼び出し先（DocumentService）と呼び出し元（BFF）が共有する唯一の写像点である ——
// 割れると片方だけが既定値を再適用する形になり、静かに壊れる。
public class DocumentReadGrpcMappingTests
{
    private static DocumentDto Doc(string? markdownUri, bool hasBody) => new()
    {
        Id = Guid.NewGuid(),
        Title = "写像の対象",
        Status = "normalized",
        MarkdownUri = markdownUri,
        Version = 3,
        Attributes = new Dictionary<string, string> { ["department"] = "sales" },
        Tags = ["経理", "手順"],
        CreatedAt = new DateTimeOffset(2026, 9, 6, 1, 2, 3, 456, TimeSpan.Zero).AddTicks(7),
        UpdatedAt = new DateTimeOffset(2026, 9, 6, 4, 5, 6, 789, TimeSpan.Zero),
        HasBody = hasBody,
    };

    // 🔴 **変異試験**: `ToProto` の `HasBody = d.HasBody` を落とす（proto3 の既定 false に任せる）と、
    // **本文ありの文書がすべて「本文なし」になる**。SC-03 は本文の位置へ
    // 「本文なし（原本を参照）」を出し、利用者には本文が消えたように見える。
    [Fact]
    public void HasBody_true_survives_the_round_trip()
    {
        var dto = Doc("storage://b/k", hasBody: true);
        DocumentReadGrpcMapping.ToDto(DocumentReadGrpcMapping.ToProto(dto)).HasBody.Should().BeTrue();
    }

    // 陽性対照の対（「常に true を返す」実装だと上のテストだけでは緑になる）。
    [Fact]
    public void HasBody_false_survives_the_round_trip()
    {
        var dto = Doc("storage://b/k", hasBody: false);
        DocumentReadGrpcMapping.ToDto(DocumentReadGrpcMapping.ToProto(dto)).HasBody.Should().BeFalse();
    }

    // 🔴 `MarkdownUri` の null と空文字は**画面で別物**である（null は「(未設定)」の縮退文言）。
    // `optional`（field presence）を無視して常に代入すると、null が "" になって区別が消える。
    [Fact]
    public void MarkdownUri_null_and_empty_are_distinguishable()
    {
        DocumentReadGrpcMapping.ToDto(DocumentReadGrpcMapping.ToProto(Doc(null, true)))
            .MarkdownUri.Should().BeNull();
        DocumentReadGrpcMapping.ToDto(DocumentReadGrpcMapping.ToProto(Doc("", true)))
            .MarkdownUri.Should().Be("");
    }

    // 文書の写像は**全項目**が往復する（順序つきのタグ・属性・時刻の tick まで）。
    [Fact]
    public void Document_round_trips_every_field()
    {
        var dto = Doc("storage://b/k", hasBody: true);
        DocumentReadGrpcMapping.ToDto(DocumentReadGrpcMapping.ToProto(dto))
            .Should().BeEquivalentTo(dto);
    }

    private static DocumentVersionDto Version(string? changeNote) => new()
    {
        DocumentId = Guid.NewGuid(),
        Version = 2,
        Title = "版の写像",
        Status = "published",
        Attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" },
        Tags = ["経理"],
        ChangeNote = changeNote,
        CreatedAt = new DateTimeOffset(2026, 9, 6, 7, 8, 9, 10, TimeSpan.Zero).AddTicks(3),
    };

    // 🔴 `ChangeNote` の null（変更メモ無し）と空文字（空のメモ）も別物である。
    [Fact]
    public void ChangeNote_null_and_empty_are_distinguishable()
    {
        DocumentReadGrpcMapping.ToDto(DocumentReadGrpcMapping.ToProto(Version(null)))
            .ChangeNote.Should().BeNull();
        DocumentReadGrpcMapping.ToDto(DocumentReadGrpcMapping.ToProto(Version("")))
            .ChangeNote.Should().Be("");
    }

    [Fact]
    public void Version_round_trips_every_field()
    {
        var dto = Version("published");
        DocumentReadGrpcMapping.ToDto(DocumentReadGrpcMapping.ToProto(dto))
            .Should().BeEquivalentTo(dto);
    }
}
