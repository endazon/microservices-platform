using WikiService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;
using WikiService.Domain.Ports;

namespace WikiService.Tests.Infrastructure.ExternalServices;

// FR-13, UC-07, IADR-0021, Issue #88: Wiki.js GraphQL クライアントのスキーマ整合（稼働 Wiki.js 2.5.314 の
// 実測応答に基づく）。PoC 実測（Issue #88 スコープ2）で判明した以下の実挙動を担保する:
//   1. 未存在ページの pages.singleByPath は `errors`（PageNotFound 6003）＋ data.singleByPath=null を返す。
//      → 例外ではなく「未存在（null）」として扱わないと、新規ページの create に到達できない。
//   2. content を省略した部分 update は 6004（Page content cannot be empty）で失敗する。
//      → アーカイブ（unpublish）は現在の content 等を取得してから全項目シェイプで update する。
//   3. update は tags 省略で 'map' エラーになり、しかも部分適用される（非トランザクショナル）。
//      → update は常に全項目を送る。
[Trait("TestKind", "Unit")]
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

        await Build(handler).UpsertPageAsync(Page(), TestContext.Current.CancellationToken);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Should().Contain("create");
    }

    // FR-13 / #1971・[[IADR-0557]] 決定 1・2: Wiki.js の今のページ（singleByPath の応答）。既定は Page() と同じ内容を持つ。
    private static string Existing(
        string title = "テスト", string content = "# 本文", string tagsJson = """[{"tag":"poc"}]""",
        bool isPublished = true, string render = "<h1>本文</h1>")
        => JsonSerializer.Serialize(new
        {
            data = new
            {
                pages = new
                {
                    singleByPath = new
                    {
                        id = 5,
                        title,
                        content,
                        render,
                        isPublished,
                        isPrivate = false,
                        tags = JsonDocument.Parse(tagsJson).RootElement,
                    },
                },
            },
        });

    private const string UpdateOk = """{"data":{"pages":{"update":{"responseResult":{"succeeded":true,"errorCode":0,"message":"ok"}}}}}""";

    // 受け入れ基準 A1・A2（#1971）: Wiki.js が同じ内容を既に持つなら update を送らない（往復は singleByPath の 1 回だけ）。
    // タグは Wiki.js の associateTags と同じ正規化（trim・小文字・重複除去・順不同）で比べる。
    [Theory]
    [InlineData(new[] { "poc" }, """[{"tag":"poc"}]""")]
    [InlineData(new[] { " POC ", "poc" }, """[{"tag":"poc"}]""")]
    [InlineData(new[] { "b", "A" }, """[{"tag":"a"},{"tag":"b"}]""")]
    [InlineData(new string[0], "[]")]
    public async Task UpsertPageAsync_SkipsUpdate_WhenWikiJsAlreadyHoldsTheSameContent(string[] tags, string heldTags)
    {
        var handler = new RecordingHandler(Existing(tagsJson: heldTags), UpdateOk);
        var page = new WikiJsPage("doc/00000000-0000-0000-0000-000000000001", "テスト", "# 本文", tags, IsPrivate: true);

        await Build(handler).UpsertPageAsync(page, TestContext.Current.CancellationToken);

        handler.Requests.Should().ContainSingle("同じ内容の update は Wiki.js の状態を変えず、描画の子プロセスに 2.5〜5 秒を使うだけである")
            .Which.Should().Contain("singleByPath").And.Contain("content").And.Contain("render").And.Contain("isPublished");
    }

    // 受け入れ基準 A3（#1971）: 比べる項目のどれかが違えば従来どおり全項目シェイプで update する。
    [Theory]
    [InlineData("title")]
    [InlineData("content")]
    [InlineData("tags-missing")]
    [InlineData("tags-extra")]
    [InlineData("unpublished")]
    [InlineData("render-empty")]
    public async Task UpsertPageAsync_Updates_WhenAnyComparedFieldDiffers(string difference)
    {
        var existing = difference switch
        {
            "title" => Existing(title: "旧い表題"),
            "content" => Existing(content: "# 旧い本文"),
            "tags-missing" => Existing(tagsJson: "[]"),
            "tags-extra" => Existing(tagsJson: """[{"tag":"poc"},{"tag":"old"}]"""),
            "unpublished" => Existing(isPublished: false),
            "render-empty" => Existing(render: ""),
            _ => throw new ArgumentOutOfRangeException(nameof(difference)),
        };
        var handler = new RecordingHandler(existing, UpdateOk);

        await Build(handler).UpsertPageAsync(Page(), TestContext.Current.CancellationToken);

        handler.Requests.Should().HaveCount(2);
        var variables = JsonDocument.Parse(handler.Requests[1]).RootElement.GetProperty("variables");
        variables.GetProperty("id").GetInt32().Should().Be(5);
        variables.GetProperty("content").GetString().Should().Be("# 本文");
        variables.GetProperty("title").GetString().Should().Be("テスト");
        variables.GetProperty("isPublished").GetBoolean().Should().BeTrue();
    }

    // 受け入れ基準 A4（#1971）: isPrivate だけの違いでは update を送らない（Wiki.js 2.5 の update は isPrivate を書かないので、送っても直らない）。
    [Fact]
    public async Task UpsertPageAsync_SkipsUpdate_WhenOnlyIsPrivateDiffers()
    {
        // Existing() は isPrivate=false。Page(isPrivate: true) を送る。
        var handler = new RecordingHandler(Existing(), UpdateOk);

        await Build(handler).UpsertPageAsync(Page(isPrivate: true), TestContext.Current.CancellationToken);

        handler.Requests.Should().ContainSingle();
    }

    // 実測 1: 認可プロキシの本文取得も、未存在（errors 6003）は null を返す（→ ゲートウェイが 404 化）。
    [Fact]
    public async Task GetRenderedContentAsync_ReturnsNull_WhenPageNotFound()
    {
        var handler = new RecordingHandler(PageNotFoundEnvelope);

        var content = await Build(handler).GetRenderedContentAsync("doc/x", TestContext.Current.CancellationToken);

        content.Should().BeNull();
    }

    // 6003 以外の GraphQL エラーは従来どおり例外（→ ブローカのリトライ）。
    [Fact]
    public async Task UpsertPageAsync_Throws_OnOtherGraphQlErrors()
    {
        var handler = new RecordingHandler(
            """{"errors":[{"message":"Forbidden","extensions":{"exception":{"code":1}}}],"data":null}""");

        var act = () => Build(handler).UpsertPageAsync(Page(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<WikiJsSyncException>();
    }

    // FR-13 テスト仕様 T-33・T-34 (#1966・[[IADR-0554]] 決定 5): 値の長さ（22001）で拒まれたのは恒久的な失敗として写し、他は一時障害のまま。
    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    public async Task UpsertPageAsync_MapsValueTooLongToInputRejected(string op)
    {
        var lookup = op == "create" ? PageNotFoundEnvelope : """{"data":{"pages":{"singleByPath":{"id":5}}}}""";
        var handler = new RecordingHandler(lookup,
            Failed(op, "insert into \"pages\" (\"title\") values ($1) - value too long for type character varying(255)"));

        var act = () => Build(handler).UpsertPageAsync(Page(), TestContext.Current.CancellationToken);

        var rejected = (await act.Should().ThrowAsync<WikiJsInputRejectedException>()).Which;
        rejected.Operation.Should().Be(op);
        rejected.Message.Should().Contain("character varying(255)");
    }

    // Wiki.js の失敗の応答（responseResult.succeeded=false・errorCode=1）。message は JSON の文字列として埋める。
    private static string Failed(string op, string message)
        => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["data"] = new
            {
                pages = new Dictionary<string, object>
                {
                    [op] = new { responseResult = new { succeeded = false, errorCode = 1, message } },
                }
            },
        });

    // 🔴 [[IADR-0554]] 残余 (a): 恒久の判定は Wiki.js（knex・pg）が返す DB の message の定型句に依存する。定型句を確かめた Wiki.js の版
    // （compose と Helm が固定する digest）が変わったらここで赤にし、上げる人に定型句の再確認を求める（変わると value too long は再試行 → DLQ へ戻る＝安全側の劣化）。
    private const string VerifiedWikiJsDigest = "sha256:fffff288a52f76e5b8c19a4fbcd0f3e538e96edea642c4f2d813d54d59a876de";

    [Fact]
    public void 値の長さの定型句を確かめたWikijsの版から動いていない()
    {
        var compose = File.ReadAllText(RepoFile("deploy/docker-compose.yml"));
        var values = File.ReadAllText(RepoFile("deploy/helm/microservices-platform/values.yaml"));

        const string why = "Wiki.js の版を上げたら、値の長さで拒んだときの message に定型句 'value too long for type ' が残ることを確かめ、"
            + "この定数を新しい digest へ更新する（運用仕様書「DLQ に溜まった Wiki 同期の失敗の再発行」）";
        compose.Should().Contain($"ghcr.io/requarks/wiki:2.5@{VerifiedWikiJsDigest}", why);
        values.Should().Contain($"digest: \"{VerifiedWikiJsDigest}\"", why);
    }

    // 解決できなければ止める（fail-closed）。
    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "deploy", "docker-compose.yml")))
                return Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
        }
        throw new InvalidOperationException("リポジトリのルート（deploy/docker-compose.yml を持つ）が見つからない。");
    }

    [Theory]
    [InlineData("connect ECONNREFUSED 10.0.0.7:5432")]
    [InlineData("Knex: Timeout acquiring a connection. The pool is probably full.")]
    [InlineData("Page content cannot be empty.")]
    public async Task UpsertPageAsync_KeepsOtherFailuresTransient(string message)
    {
        var handler = new RecordingHandler(PageNotFoundEnvelope,
            Failed("create", message));

        var act = () => Build(handler).UpsertPageAsync(Page(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<WikiJsSyncException>();
    }

    // 実測 2・3: アーカイブは現在の content/title/tags を取得し、全項目シェイプ＋ isPublished:false で update する。
    [Fact]
    public async Task ArchivePageAsync_SendsFullUpdateShape_WithUnpublish()
    {
        var handler = new RecordingHandler(
            """{"data":{"pages":{"singleByPath":{"id":3,"content":"# 本文","title":"規程","tags":[{"tag":"poc"}]}}}}""",
            """{"data":{"pages":{"update":{"responseResult":{"succeeded":true,"errorCode":0,"message":"ok"}}}}}""");

        await Build(handler).ArchivePageAsync("doc/x", TestContext.Current.CancellationToken);

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

        await Build(handler).ArchivePageAsync("doc/x", TestContext.Current.CancellationToken);

        handler.Requests.Should().HaveCount(1);
    }

    // FR-13, IADR-0563 決定 3 (#1971): 既に非公開（isPublished=false）のページへは update を送らない（往復は singleByPath の 1 回だけ）。
    // 一括アーカイブの後の再発行は、ほぼこの形である（update は内容が同じでも描画の子プロセスを起こす）。
    [Fact]
    public async Task ArchivePageAsync_Skips_WhenAlreadyUnpublished()
    {
        var handler = new RecordingHandler(
            """{"data":{"pages":{"singleByPath":{"id":3,"content":"# 本文","title":"規程","isPublished":false,"tags":[{"tag":"poc"}]}}}}""",
            """{"data":{"pages":{"update":{"responseResult":{"succeeded":true,"errorCode":0,"message":"ok"}}}}}""");

        await Build(handler).ArchivePageAsync("doc/x", TestContext.Current.CancellationToken);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Should().Contain("isPublished", "公開状態を同じ 1 往復で取る");
    }

    // 対照: 公開中（true）・公開状態が読めない（欠落・null）なら、従来どおり unpublish の update を送る（安全側）。
    [Theory]
    [InlineData(",\"isPublished\":true")]
    [InlineData(",\"isPublished\":null")]
    [InlineData("")]
    public async Task ArchivePageAsync_Updates_UnlessKnownUnpublished(string publishedJson)
    {
        var handler = new RecordingHandler(
            "{\"data\":{\"pages\":{\"singleByPath\":{\"id\":3,\"content\":\"# 本文\",\"title\":\"規程\""
                + publishedJson + ",\"tags\":[{\"tag\":\"poc\"}]}}}}",
            """{"data":{"pages":{"update":{"responseResult":{"succeeded":true,"errorCode":0,"message":"ok"}}}}}""");

        await Build(handler).ArchivePageAsync("doc/x", TestContext.Current.CancellationToken);

        handler.Requests.Should().HaveCount(2);
        JsonDocument.Parse(handler.Requests[1]).RootElement.GetProperty("variables")
            .GetProperty("isPublished").GetBoolean().Should().BeFalse();
    }

    // 削除: 存在すれば pages.delete、未存在は冪等にスキップ。
    [Fact]
    public async Task DeletePageAsync_DeletesById_WhenPageExists()
    {
        var handler = new RecordingHandler(
            """{"data":{"pages":{"singleByPath":{"id":7}}}}""",
            """{"data":{"pages":{"delete":{"responseResult":{"succeeded":true,"errorCode":0,"message":"ok"}}}}}""");

        await Build(handler).DeletePageAsync("doc/x", TestContext.Current.CancellationToken);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Should().Contain("delete");
    }

    [Fact]
    public async Task DeletePageAsync_Skips_WhenPageNotFound()
    {
        var handler = new RecordingHandler(PageNotFoundEnvelope);

        await Build(handler).DeletePageAsync("doc/x", TestContext.Current.CancellationToken);

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
