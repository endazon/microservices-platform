using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using WikiService.Infrastructure.ExternalServices;

namespace WikiService.Tests.Features.Wiki.SearchPages;

// UC-07 基本フロー 1「検索する」, FR-13, ADR-0011, IADR-0021, IADR-0335（#1126）:
// Wiki.js 2.x の `pages.search` 応答形の取り扱い。**スキーマ整合を実測した応答形で固定する**
// （既存の `WikiJsGraphQlClientTests` が upsert / 本文取得に対して行っているのと同じ作法）。
public class WikiJsSearchClientTests
{
    private static WikiJsGraphQlClient Build(RecordingHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("http://wiki-js.test/graphql") },
            NullLogger<WikiJsGraphQlClient>.Instance);

    // Wiki.js 2.x の `pages.search` は results[] を返す。前段が使うのは path である。
    [Fact]
    public async Task SearchAsync_MapsResultsToPaths()
    {
        var handler = new RecordingHandler("""
            {"data":{"pages":{"search":{
              "results":[
                {"title":"公開規程","path":"doc/00000000-0000-0000-0000-000000000001","locale":"ja"},
                {"title":"機密規程","path":"/doc/00000000-0000-0000-0000-000000000002","locale":"ja"}],
              "totalHits":2}}}}
            """);

        var hits = await Build(handler).SearchAsync("規程", TestContext.Current.CancellationToken);

        hits.Select(h => h.Path).Should().ContainInOrder(
            "doc/00000000-0000-0000-0000-000000000001",
            // 先頭スラッシュは正規化して返す（台帳の正準パスと同じ形にそろえる）。
            "doc/00000000-0000-0000-0000-000000000002");
    }

    // 0 件は空配列であって、例外でも null でもない。
    [Fact]
    public async Task SearchAsync_ReturnsEmpty_WhenNoHits()
    {
        var handler = new RecordingHandler("""
            {"data":{"pages":{"search":{"results":[],"totalHits":0}}}}
            """);

        var hits = await Build(handler).SearchAsync("該当なし", TestContext.Current.CancellationToken);

        hits.Should().BeEmpty();
    }

    // path を持たない要素は落とす（応答形の揺れで NullReferenceException にしない）。
    [Fact]
    public async Task SearchAsync_SkipsResultsWithoutPath()
    {
        var handler = new RecordingHandler("""
            {"data":{"pages":{"search":{
              "results":[{"title":"壊れた行","locale":"ja"},
                         {"title":"公開規程","path":"doc/00000000-0000-0000-0000-000000000001","locale":"ja"}],
              "totalHits":2}}}}
            """);

        var hits = await Build(handler).SearchAsync("規程", TestContext.Current.CancellationToken);

        hits.Should().ContainSingle().Which.Path.Should().Be("doc/00000000-0000-0000-0000-000000000001");
    }

    // 🔴 GraphQL エラーは例外にする。**空配列へ握り潰さない** —— 呼び出し側が 502 へ写し、
    // 「壊れている」を「該当が無い」に見せない（IADR-0256 と同じ切り分け）。
    [Fact]
    public async Task SearchAsync_Throws_OnGraphQlError()
    {
        var handler = new RecordingHandler("""
            {"errors":[{"message":"Search engine is not configured."}]}
            """);

        var act = async () => await Build(handler).SearchAsync("規程", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<WikiJsSyncException>();
    }
}
