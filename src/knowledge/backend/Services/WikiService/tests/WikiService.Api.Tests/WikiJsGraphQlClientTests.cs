using WikiService.Api.Composable.Adapters;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;
using WikiService.Api.Foundation.Ports;
using WikiService.Api.Foundation.Services;

namespace WikiService.Api.Tests;

// FR-13, UC-07, IADR-0021, Issue #88: Wiki.js GraphQL クライアントのスキーマ整合（稼働 Wiki.js 2.5.314 の
// 実測応答に基づく）。PoC 実測（Issue #88 スコープ2）で判明した以下の実挙動を担保する:
//   1. 未存在ページの pages.singleByPath は `errors`（PageNotFound 6003）＋ data.singleByPath=null を返す。
//      → 例外ではなく「未存在（null）」として扱わないと、新規ページの create に到達できない。
//   2. content を省略した部分 update は 6004（Page content cannot be empty）で失敗する。
//      → アーカイブ（unpublish）は現在の content 等を取得してから全項目シェイプで update する。
//   3. update は tags 省略で 'map' エラーになり、しかも部分適用される（非トランザクショナル）。
//      → update は常に全項目を送る。
public class WikiJsGraphQlClientTests
{
    private static readonly string PageNotFoundEnvelope = """
        {"errors":[{"message":"This page does not exist.","path":["pages","singleByPath"],
          "extensions":{"code":"INTERNAL_SERVER_ERROR","exception":{"code":6003,"message":"This page does not exist."}}}],
         "data":{"pages":{"singleByPath":null}}}
        """;

    private static WikiJsGraphQlClient Build(RecordingHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("http://wiki-js.test/graphql") },
            NullLogger<WikiJsGraphQlClient>.Instance);

    private static WikiJsPage Page(bool isPrivate = true)
        => new("doc/00000000-0000-0000-0000-000000000001", "テスト", "# 本文", ["poc"], isPrivate);

    // 実測 1: 未存在の singleByPath（errors 6003）→ 例外にせず create へ進む。
    [Fact]
    public async Task UpsertPageAsync_CreatesPage_WhenSingleByPathReturnsPageNotFoundError()
    {
        var handler = new RecordingHandler(
            PageNotFoundEnvelope,
            """{"data":{"pages":{"create":{"responseResult":{"succeeded":true,"errorCode":0,"message":"ok"}}}}}""");

        await Build(handler).UpsertPageAsync(Page());

        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Should().Contain("create");
    }

    // 実測 1: 認可プロキシの本文取得も、未存在（errors 6003）は null を返す（→ ゲートウェイが 404 化）。
    [Fact]
    public async Task GetRenderedContentAsync_ReturnsNull_WhenPageNotFound()
    {
        var handler = new RecordingHandler(PageNotFoundEnvelope);

        var content = await Build(handler).GetRenderedContentAsync("doc/x");

        content.Should().BeNull();
    }

    // 6003 以外の GraphQL エラーは従来どおり例外（→ MassTransit リトライ）。
    [Fact]
    public async Task UpsertPageAsync_Throws_OnOtherGraphQlErrors()
    {
        var handler = new RecordingHandler(
            """{"errors":[{"message":"Forbidden","extensions":{"exception":{"code":1}}}],"data":null}""");

        var act = () => Build(handler).UpsertPageAsync(Page());

        await act.Should().ThrowAsync<WikiJsSyncException>();
    }

    // 実測 2・3: アーカイブは現在の content/title/tags を取得し、全項目シェイプ＋ isPublished:false で update する。
    [Fact]
    public async Task ArchivePageAsync_SendsFullUpdateShape_WithUnpublish()
    {
        var handler = new RecordingHandler(
            """{"data":{"pages":{"singleByPath":{"id":3,"content":"# 本文","title":"規程","tags":[{"tag":"poc"}]}}}}""",
            """{"data":{"pages":{"update":{"responseResult":{"succeeded":true,"errorCode":0,"message":"ok"}}}}}""");

        await Build(handler).ArchivePageAsync("doc/x");

        handler.Requests.Should().HaveCount(2);
        var update = JsonDocument.Parse(handler.Requests[1]).RootElement;
        var variables = update.GetProperty("variables");
        // content 省略は 6004、tags 省略は 'map' エラー（部分適用）になるため、全項目を送る。
        variables.GetProperty("content").GetString().Should().Be("# 本文");
        variables.GetProperty("title").GetString().Should().Be("規程");
        variables.GetProperty("tags").GetArrayLength().Should().Be(1);
        variables.GetProperty("isPublished").GetBoolean().Should().BeFalse();
    }

    // アーカイブ: 未存在ページは冪等にスキップ（update を送らない）。
    [Fact]
    public async Task ArchivePageAsync_Skips_WhenPageNotFound()
    {
        var handler = new RecordingHandler(PageNotFoundEnvelope);

        await Build(handler).ArchivePageAsync("doc/x");

        handler.Requests.Should().HaveCount(1);
    }

    // 削除: 存在すれば pages.delete、未存在は冪等にスキップ。
    [Fact]
    public async Task DeletePageAsync_DeletesById_WhenPageExists()
    {
        var handler = new RecordingHandler(
            """{"data":{"pages":{"singleByPath":{"id":7}}}}""",
            """{"data":{"pages":{"delete":{"responseResult":{"succeeded":true,"errorCode":0,"message":"ok"}}}}}""");

        await Build(handler).DeletePageAsync("doc/x");

        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Should().Contain("delete");
    }

    [Fact]
    public async Task DeletePageAsync_Skips_WhenPageNotFound()
    {
        var handler = new RecordingHandler(PageNotFoundEnvelope);

        await Build(handler).DeletePageAsync("doc/x");

        handler.Requests.Should().HaveCount(1);
    }
}

// GraphQL 応答を順に返し、リクエスト本文を記録するテスト用ハンドラ。
internal sealed class RecordingHandler(params string[] responses) : HttpMessageHandler
{
    private int _index;
    public List<string> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(await request.Content!.ReadAsStringAsync(ct));
        var body = responses[Math.Min(_index++, responses.Length - 1)];
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
