using AiAnalysisService.Api.Foundation.Services;
using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Dtos;

namespace AiAnalysisService.Api.Tests;

// FR-04, UC-01, UC-02: 検索結果→番号付き出典（元文書リンク）の写像を検証する
public class CitationMapperTests
{
    private static SearchResultDto Result(string title, string? uri, string text = "本文") =>
        new(
            ChunkId: Guid.NewGuid(),
            DocumentId: Guid.NewGuid(),
            DocumentTitle: title,
            Text: text,
            Score: 0.9f,
            MarkdownUri: uri,
            Attributes: new Dictionary<string, string>(),
            Tags: []);

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
}
