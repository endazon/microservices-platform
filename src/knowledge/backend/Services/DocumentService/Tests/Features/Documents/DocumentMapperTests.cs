using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.Documents;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Tests.Features.Documents;

// FR-06, UC-03, SC-03, 計画 ADR-0030 §決定（マッピング = Riok.Mapperly）/ IADR-0371 決定 3 /
// IADR-0393 / IADR-0405: 手書きの詰め替えを生成マッパへ置き換えた際の**振る舞い同値**を固定する。
//
// 🔴 **生成物を信じるのではなく、写った値を見る。** Mapperly は名前が一致しないプロパティを
// 黙って落とすことがあり、**列が 1 つ抜けても型は通る**。10 列 / 8 列を 1 つずつ見る。
[Trait("TestKind", "Unit")]
public class DocumentMapperTests
{
    private static Document NewDocument(List<Guid>? tags = null) =>
        Document.CreateNormalized(
            Guid.NewGuid(), "四半期報告", "storage://docs/body.md",
            attributes: new Dictionary<string, string> { ["doc_scope"] = "public" },
            tags: tags ?? [],
            contentFingerprint: "fp-1",
            assetUris: ["storage://docs/a.png"],
            hasBody: true,
            originalPath: "/in/q1.pdf",
            dataSourceName: "shared-drive");

    // 陽性: `DocumentDto` の全 10 列が値を保ったまま写る（タグ名は追加引数から入る）。
    [Fact]
    public void ToDto_CopiesEveryProperty()
    {
        var d = NewDocument();

        var dto = DocumentMapper.ToDto(d, ["営業", "総務"]);

        dto.Id.Should().Be(d.Id);
        dto.Title.Should().Be("四半期報告");
        dto.Status.Should().Be(DocumentStatus.Normalized);
        dto.MarkdownUri.Should().Be("storage://docs/body.md");
        dto.Version.Should().Be(d.Version);
        dto.Attributes.Should().BeEquivalentTo(new Dictionary<string, string> { ["doc_scope"] = "public" });
        dto.Tags.Should().Equal("営業", "総務");
        dto.CreatedAt.Should().Be(d.CreatedAt);
        dto.UpdatedAt.Should().Be(d.UpdatedAt);
        dto.HasBody.Should().BeTrue();
    }

    // 🔴 **タグは追加引数がそのまま載る**（源の `List<Guid>` は写らない）。
    // 追加引数を渡し損ねた実装は GUID 文字列を返す —— それを RMG082 の error 化が止めているが、
    // この試験は「**渡した名前が、渡した順で載る**」ことそのものを固定する。
    [Fact]
    public void ToDto_PutsResolvedTagNamesNotIds()
    {
        var tagId = Guid.NewGuid();
        var d = NewDocument([tagId]);

        var dto = DocumentMapper.ToDto(d, ["経理"]);

        dto.Tags.Should().Equal("経理");
        dto.Tags.Should().NotContain(tagId.ToString());
    }

    // 陰性: 任意項目の null は null のまま写る。
    // **`HasBody` の既定は `true`** なので、`false` が写ることを別に見る（既定へ倒れない）。
    [Fact]
    public void ToDto_KeepsNullOptionalFieldsAndFalseHasBody()
    {
        var d = Document.Create("下書き", originalUri: null, contentType: null);

        var dto = DocumentMapper.ToDto(d, []);

        dto.MarkdownUri.Should().BeNull();
        dto.Tags.Should().BeEmpty();
        dto.Attributes.Should().BeEmpty();
        dto.Status.Should().Be(DocumentStatus.Draft);

        var noBody = Document.CreateNormalized(
            Guid.NewGuid(), "本文なし PDF", "storage://docs/none.md", hasBody: false);

        DocumentMapper.ToDto(noBody, []).HasBody.Should().BeFalse();
    }

    // 陽性: `DocumentVersionDto` の全 8 列が写る。
    [Fact]
    public void ToVersionDto_CopiesEveryProperty()
    {
        var d = NewDocument();
        var v = d.Versions[^1];

        var dto = DocumentMapper.ToVersionDto(v, ["営業"]);

        dto.DocumentId.Should().Be(d.Id);
        dto.Version.Should().Be(v.Version);
        dto.Title.Should().Be("四半期報告");
        dto.Status.Should().Be(DocumentStatus.Normalized);
        dto.Attributes.Should().BeEquivalentTo(new Dictionary<string, string> { ["doc_scope"] = "public" });
        dto.Tags.Should().Equal("営業");
        dto.ChangeNote.Should().Be("normalized");
        dto.CreatedAt.Should().Be(v.CreatedAt);
    }

    // 🔴 **版に本文の参照は載らない**（#1011 / IADR-0290 決定 1・2）。
    // 源（`DocumentVersion`）は `MarkdownUri` を**持っている** —— 落とすと決めたのであって、
    // たまたま無いのではない。生成マッパの `[MapperIgnoreSource]` がその宣言であり、
    // `DocumentVersionDto` に欄を戻すと **RMG012 でビルドが止まる**（本 PR で実測）。
    // この試験は契約側の事実そのものを固定する（ビルドの門が将来抑止されても残る層）。
    [Fact]
    public void Dto_HasNoMarkdownUriMember()
    {
        typeof(DocumentVersion).GetProperty("MarkdownUri").Should().NotBeNull(
            "源は本文 URI を持つ（落とすと決めた対象が実在することの陽性対照）");

        typeof(DocumentVersionDto).GetProperty("MarkdownUri").Should().BeNull(
            "版の本文参照は常に現行版を指すので応答へ載せない（#1011 / IADR-0290）");
    }
}
