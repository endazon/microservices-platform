using AwesomeAssertions;
using WikiService.Domain;

namespace WikiService.Tests.Domain;

// UC-07, FR-13, IADR-0021, IADR-0331（#1126）: 正準パス `doc/<documentId>` の往復。
// 検索は Wiki.js が返した**パス**から台帳の行を引き当てるため、`PathFor` の逆写像が要る。
// **台帳に足場を持たない形（人手で作られたページ・別の名前空間）は落とす** ——
// ABAC で判定できないものを可視にしないための、検索経路の最初の関門である。
public class WikiPagePathTests
{
    // 往復: `PathFor` が作ったパスは必ず元の ID へ戻る（陽性対照）。
    [Fact]
    public void TryParseDocumentId_RoundTripsPathFor()
    {
        var id = Guid.NewGuid();

        WikiPage.TryParseDocumentId(WikiPage.PathFor(id), out var parsed).Should().BeTrue();

        parsed.Should().Be(id);
    }

    // 先頭スラッシュ・前置の大小文字は吸収する（Wiki.js の応答が `/doc/...` を返しても引き当てる）。
    [Theory]
    [InlineData("/doc/{0}")]
    [InlineData("doc/{0}/")]
    [InlineData("DOC/{0}")]
    public void TryParseDocumentId_AcceptsNormalizableForms(string template)
    {
        var id = Guid.NewGuid();

        WikiPage.TryParseDocumentId(string.Format(template, id), out var parsed).Should().BeTrue();

        parsed.Should().Be(id);
    }

    // 否定形: 台帳の導出規則に合わないパスは引き当てない。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("home")]
    [InlineData("doc")]
    [InlineData("doc/not-a-guid")]
    [InlineData("other/00000000-0000-0000-0000-000000000001")]
    [InlineData("doc/00000000-0000-0000-0000-000000000001/child")]
    public void TryParseDocumentId_RejectsForeignPaths(string path)
    {
        WikiPage.TryParseDocumentId(path, out var parsed).Should().BeFalse();

        parsed.Should().Be(Guid.Empty);
    }

    [Fact]
    public void TryParseDocumentId_RejectsNull()
    {
        WikiPage.TryParseDocumentId(null, out _).Should().BeFalse();
    }
}
