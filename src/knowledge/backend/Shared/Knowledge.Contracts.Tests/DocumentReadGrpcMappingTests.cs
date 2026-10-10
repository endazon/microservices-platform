using System.Reflection;
using AwesomeAssertions;
using Google.Protobuf;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Grpc;
using Xunit;
using Pb = Knowledge.Contracts.Grpc.Document.V1;

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

    // FR-06, ADR-0050 決定 1 (#1575): 本文指紋は値も null も往復する。
    // 🔴 **変異試験**: presence を無視して常に代入すると null が "" になり、「本文なし」が
    // 「指紋は空文字」へ化ける（呼び出し側は空文字を指紋として突き合わせ、常に不一致と判定する）。
    // 代入を落とすと値が消え、gRPC 経路の BFF だけが指紋を持たない応答を返す。
    [Fact]
    public void ContentFingerprint_value_and_null_survive_the_round_trip()
    {
        var withFingerprint = Doc("storage://b/k", hasBody: true) with { ContentFingerprint = new string('a', 64) };
        DocumentReadGrpcMapping.ToDto(DocumentReadGrpcMapping.ToProto(withFingerprint))
            .ContentFingerprint.Should().Be(new string('a', 64));

        DocumentReadGrpcMapping.ToDto(DocumentReadGrpcMapping.ToProto(Doc(null, true)))
            .ContentFingerprint.Should().BeNull();
    }

    // 文書の写像は**全項目**が往復する（順序つきのタグ・属性・時刻の tick まで）。
    // FR-06, FR-19 (#1898): 器は**全項目を既定値以外**で埋める（下の `Every_field_fixture_...` が保証する）。
    // 以前の器は `SharedWith` を null のままにしていたため、写し漏れ（#1898 の回帰）でもこの試験は緑だった。
    [Fact]
    public void Document_round_trips_every_field()
    {
        var dto = EveryField();
        DocumentReadGrpcMapping.ToDto(DocumentReadGrpcMapping.ToProto(dto))
            .Should().BeEquivalentTo(dto, o => o.WithStrictOrdering());
    }

    // 🔴 線上（バイト列）を経由しても全項目が往復する。生成コードの既定値・presence の扱いまで含めて固定する。
    [Fact]
    public void Document_round_trips_every_field_through_the_wire()
    {
        var dto = EveryField();
        var bytes = DocumentReadGrpcMapping.ToProto(dto).ToByteArray();
        DocumentReadGrpcMapping.ToDto(Pb.DocumentSummary.Parser.ParseFrom(bytes))
            .Should().BeEquivalentTo(dto, o => o.WithStrictOrdering());
    }

    // FR-06, FR-19 (#1898): **同型の再発防止。** `DocumentDto` に項目を足したのに器へ入れ忘れると、
    // 上の全項目往復は「既定値どうしの一致」で緑になり、写し漏れを見逃す（#1898 がまさにこの形だった）。
    // 器の全公開プロパティが `new DocumentDto()` の既定値と異なることを反射で確かめる。
    [Fact]
    public void Every_field_fixture_sets_every_property_to_a_non_default_value()
    {
        var fixture = EveryField();
        var defaults = new DocumentDto();

        foreach (var p in typeof(DocumentDto).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var value = p.GetValue(fixture);
            var @default = p.GetValue(defaults);
            value.Should().NotBeNull($"器の {p.Name} が null のままだと写し漏れを検出できない");
            if (value is System.Collections.IEnumerable seq and not string)
                seq.Cast<object>().Should().NotBeEmpty($"器の {p.Name} が空だと写し漏れを検出できない");
            else
                value.Should().NotBe(@default, $"器の {p.Name} が既定値のままだと写し漏れを検出できない");
        }
    }

    // FR-06, FR-19, ADR-0036 D-06, ADR-0098 決定 1, [[IADR-0447]] (#1898): 共有先（複数件）は**値も順序も**往復する。
    // 🔴 **変異試験**: `ToProto` の `m.SharedWith.AddRange(...)` か `ToDto` の `SharedWith = ...` を落とすと
    // null になり、BFF の共有先ベースの分岐が gRPC 経路でだけ一致しない（共有された相手が SC-03 で 404）。
    [Fact]
    public void SharedWith_with_multiple_subjects_survives_the_round_trip_in_order()
    {
        var dto = Doc("storage://b/k", hasBody: true) with
        {
            SharedWith = ["alice", "11111111-1111-1111-1111-111111111111", "bob"],
        };

        var proto = DocumentReadGrpcMapping.ToProto(dto);
        proto.SharedWith.Should().Equal("alice", "11111111-1111-1111-1111-111111111111", "bob");
        DocumentReadGrpcMapping.ToDto(proto).SharedWith
            .Should().Equal("alice", "11111111-1111-1111-1111-111111111111", "bob");
    }

    // 1 件でも往復する（「2 件以上のときだけ写す」型の境界の誤りを落とす）。
    [Fact]
    public void SharedWith_with_a_single_subject_survives_the_round_trip()
    {
        var dto = Doc("storage://b/k", hasBody: true) with { SharedWith = ["alice"] };
        DocumentReadGrpcMapping.ToDto(DocumentReadGrpcMapping.ToProto(dto)).SharedWith
            .Should().Equal("alice");
    }

    // 共有なし（null）は null のまま返る。🔴 **空リストへ化けさせない** —— `DocumentDto.SharedWith` の契約は
    // 「null＝共有なし」であり、REST（`DocumentEndpoints.NullIfEmpty`）も null を返す。
    [Fact]
    public void SharedWith_null_stays_null()
    {
        var proto = DocumentReadGrpcMapping.ToProto(Doc(null, true));
        proto.SharedWith.Should().BeEmpty();
        DocumentReadGrpcMapping.ToDto(proto).SharedWith.Should().BeNull();
    }

    // 空リストは線上で null と区別できない（repeated に presence は無い）ので、**null へ正規化される**。
    // サーバは空リストを作らない（`NullIfEmpty`）ため REST と同じ形であり、BFF の読み（`WithSharedWith` は
    // 空集合を載せない・`ForViewer` は null を「共有なし」と読む）とも同値である。
    [Fact]
    public void SharedWith_empty_is_normalized_to_null()
    {
        var dto = Doc(null, true) with { SharedWith = [] };
        DocumentReadGrpcMapping.ToDto(DocumentReadGrpcMapping.ToProto(dto)).SharedWith.Should().BeNull();
    }

    private static DocumentDto EveryField() => Doc("storage://b/k", hasBody: false) with
    {
        // `HasBody` の既定は true なので、既定値以外（false）で埋める。
        SharedWith = ["alice", "g-1"],
        ContentFingerprint = new string('f', 64),
    };

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
