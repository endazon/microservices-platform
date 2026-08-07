using AiAnalysisService.Api.Foundation.Services;
using FluentAssertions;
using Knowledge.Contracts.Dtos;

namespace AiAnalysisService.Api.Tests;

// FR-04, UC-01, UC-02: 検索結果→番号付き出典（元文書リンク）の写像を検証する
public class CitationMapperTests
{
    private static SearchResultDto Result(string title, string? uri, string text = "本文",
        Dictionary<string, string>? attributes = null) =>
        new(
            ChunkId: Guid.NewGuid(),
            DocumentId: Guid.NewGuid(),
            DocumentTitle: title,
            Text: text,
            Score: 0.9f,
            MarkdownUri: uri,
            Attributes: attributes ?? new Dictionary<string, string>(),
            Tags: []);

    // FR-04, SC-01, #541: 機密区分属性を持つ検索結果。
    private static SearchResultDto ResultWithConfidentiality(string? level) =>
        Result("文書A", null, "本文",
            level is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["confidentiality"] = level });

    [Fact]
    public void ToCitations_AssignsSequentialNumbersFrom1()
    {
        var results = new List<SearchResultDto>
        {
            Result("文書A", "s3://bucket/a.md"),
            Result("文書B", "s3://bucket/b.md"),
            Result("文書C", "s3://bucket/c.md"),
        };

        var citations = CitationMapper.ToCitations(results);

        citations.Select(c => c.Number).Should().Equal(1, 2, 3);
        citations[0].DocumentTitle.Should().Be("文書A");
    }

    [Fact]
    public void ToCitations_UsesMarkdownUriAsSourceLink()
    {
        var citations = CitationMapper.ToCitations(new[] { Result("文書A", "s3://bucket/a.md") });

        citations[0].SourceUri.Should().Be("s3://bucket/a.md");
    }

    [Fact]
    public void ToCitations_FallsBackToDocumentRoute_WhenNoMarkdownUri()
    {
        var result = Result("文書A", null);

        var citations = CitationMapper.ToCitations(new[] { result });

        citations[0].SourceUri.Should().Be($"/documents/{result.DocumentId}");
    }

    [Fact]
    public void ToCitations_TruncatesLongSnippet()
    {
        var longText = new string('あ', 500);

        var citations = CitationMapper.ToCitations(new[] { Result("文書A", null, longText) });

        citations[0].Snippet.Length.Should().BeLessThan(longText.Length);
        citations[0].Snippet.Should().EndWith("…");
    }

    [Fact]
    public void BuildContext_NumbersMatchCitations()
    {
        var citations = CitationMapper.ToCitations(new[]
        {
            Result("文書A", "s3://a.md", "Aの内容"),
            Result("文書B", "s3://b.md", "Bの内容"),
        });

        var context = CitationMapper.BuildContext(citations);

        context.Should().Contain("[1] 文書A");
        context.Should().Contain("[2] 文書B");
    }

    [Fact]
    public void ToCitations_EmptyResults_ReturnsEmpty()
    {
        CitationMapper.ToCitations([]).Should().BeEmpty();
    }

    // T-17 / FR-04, SC-01, #541: 文書属性の機密区分がそのまま出典へ載る（4 値すべて）。
    [Theory]
    [InlineData("public")]
    [InlineData("internal")]
    [InlineData("confidential")]
    [InlineData("restricted")]
    public void ToCitations_CarriesConfidentialityFromAttributes(string level)
    {
        var citations = CitationMapper.ToCitations([ResultWithConfidentiality(level)]);

        citations[0].Confidentiality.Should().Be(level);
    }

    // T-18 / FR-04, FR-05, SC-01, #541: 属性の欠落・空文字・未知値は安全側（restricted）へ縮退する。
    // 08_data-egress-policy「既定は安全側」。過剰公開は「社外へ引用してよい」の判断を誤らせる。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("top-secret")]
    public void ToCitations_FallsBackToRestricted_WhenConfidentialityMissingOrUnknown(string? level)
    {
        var citations = CitationMapper.ToCitations([ResultWithConfidentiality(level)]);

        citations[0].Confidentiality.Should().Be("restricted");
    }

    // T-19 / #541: 大小文字違いは正準の小文字へ正規化する（表示・照合の揺れを持ち込まない）。
    [Fact]
    public void ToCitations_NormalizesConfidentialityCasing()
    {
        var citations = CitationMapper.ToCitations([ResultWithConfidentiality("Internal")]);

        citations[0].Confidentiality.Should().Be("internal");
    }

    // T-20 / FR-04, SC-01, #541: 機密区分は出典ごとに独立する（1 件目の区分が 2 件目へ漏れない）。
    [Fact]
    public void ToCitations_AssignsConfidentialityPerCitation()
    {
        var citations = CitationMapper.ToCitations(new[]
        {
            ResultWithConfidentiality("public"),
            ResultWithConfidentiality("confidential"),
            ResultWithConfidentiality(null),
        });

        citations.Select(c => c.Confidentiality)
            .Should().Equal("public", "confidential", "restricted");
    }

    // T-21 / FR-05, FR-11, #541: 値集合そのものの回帰。4 値であること・安全側の既定が restricted であること。
    // 表示名（公開 / 社内限 / 秘 / 取扱制限）は計画リポジトリの用語集が正であり、ここでは定義しない。
    [Fact]
    public void ConfidentialityLevels_HasFourValues_AndFailsSafeToRestricted()
    {
        ConfidentialityLevels.All.Should().Equal("public", "internal", "confidential", "restricted");
        ConfidentialityLevels.SafeDefault.Should().Be("restricted");
        ConfidentialityLevels.Rank("public").Should().BeLessThan(ConfidentialityLevels.Rank("restricted"));
        // 既定値つき位置引数のため、機密区分を渡さない組み立ては安全側になる（非破壊追加の帰結）。
        new CitationDto(1, Guid.NewGuid(), "文書A", Guid.NewGuid(), null, 0.1f, "抜粋")
            .Confidentiality.Should().Be("restricted");
    }
}
