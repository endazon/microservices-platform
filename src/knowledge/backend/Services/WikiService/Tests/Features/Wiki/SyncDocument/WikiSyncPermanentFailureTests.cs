using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Messaging;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
using WikiService.Common.Observability;
using WikiService.Domain;
using WikiService.Domain.Ports;
using WikiService.Features.Wiki;
using WikiService.Features.Wiki.SyncDocument;
using WikiService.Infrastructure.ExternalServices;
using WikiService.Infrastructure.Persistence;
using Wolverine;
using Wolverine.Tracking;

namespace WikiService.Tests.Features.Wiki.SyncDocument;

// FR-13 テスト仕様 T-25〜T-29 (#1934) —— UC-07, NFR-06, [[IADR-0541]]（本文の欠落の判定は [[IADR-0540]] と同じ）:
// **Wiki 同期の slug を文書 ID 由来の接尾辞で一意にし、再試行で直らない失敗は DLQ へ送らず記録して ack する。**
//
// - T-25（slug の一意化）: 同名の題名の別文書は両方ページになる。同じ文書の再同期は 1 ページのまま更新し slug を保つ。
// - T-26（保存時の競合）: slug の 23505 は文書 ID 32 桁へ付け替えて保存し直す。それでも当たれば Warning＋計数で ack。
//   文書 ID の 23505・接続断は例外のまま（再試行へ）。
// - T-27（本文の欠落の分類）・T-28（受け口）: NoSuchKey だけを欠落とし、Warning＋計数で ack。何も書かない。
// - T-30〜T-34（#1966・[[IADR-0554]]）: Wiki.js の表題は 255 文字（符号位置）へ文字の境界で切り詰め、印を付けて数える。
//   Wiki.js が値の長さで拒めば Warning＋計数で ack（実配送で DLQ へ行かない）。他の Wiki.js の失敗は再試行。
// - T-29（実配送）: 本番と同じ共通既定の Wolverine で、恒久的な失敗は 1 回の試行で終わり DLQ へ行かない。DB の接続断は再試行される。
//
// 🔴 InMemory は一意索引を強制しない。**一意性は「slug が互いに異なる」ことで測り**、23505 は保存の割り込み（`ScriptedSaveFailures`）で再現する。
[Trait("TestKind", "Unit")]
public class WikiSyncPermanentFailureTests
{
    private static readonly Guid DocA = Guid.Parse("19341934-0000-0000-0000-00000000000a");
    private static readonly Guid DocB = Guid.Parse("19341934-0000-0000-0000-00000000000b");
    private const string BodyUri = "storage://knowledge/docs/2026/09/old-news.md";
    private static readonly DateTimeOffset UpdatedAt = new(2026, 9, 20, 3, 4, 5, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DocumentUpdated Event(Guid documentId, string title, string? markdownUri = "s3://b/doc.md") => new(
        documentId, title, "published", markdownUri,
        new Dictionary<string, string> { ["confidentiality"] = "internal" }, ["ops"], UpdatedAt);

    // ---- T-25: slug の一意化 ----

    [Fact]
    public async Task 同名の題名の別文書は両方ページになり接尾辞で一意になる()
    {
        await using var fx = new Fixture();

        await fx.HandleAsync(Event(DocA, "経費精算規程"));
        await fx.HandleAsync(Event(DocB, "経費精算規程"));

        var pages = fx.Pages();
        pages.Should().HaveCount(2);
        pages.Select(p => p.Slug).Should().OnlyHaveUniqueItems();
        // 先に来た方は題名だけの slug（既存の URL の形を変えない）。後から来た方に文書 ID の先頭 8 桁が付く。
        pages.Single(p => p.DocumentId == DocA).Slug.Should().Be("経費精算規程");
        pages.Single(p => p.DocumentId == DocB).Slug.Should().Be($"経費精算規程-{DocB:N}"[..("経費精算規程".Length + 9)]);
        fx.Wiki.Pushed.Select(p => p.Path).Should().Equal($"doc/{DocA}", $"doc/{DocB}");
    }

    [Fact]
    public async Task 同じ文書の再同期は一ページのまま更新しslugを保つ()
    {
        await using var fx = new Fixture();

        await fx.HandleAsync(Event(DocA, "入社手続き"));
        var first = fx.Pages().Single();
        await fx.HandleAsync(Event(DocA, "入社手続き"));

        var page = fx.Pages().Should().ContainSingle().Which;
        page.Id.Should().Be(first.Id, "重複して作らず、既存の行を更新する");
        page.Slug.Should().Be("入社手続き");
        fx.Wiki.Pushed.Should().HaveCount(2);
    }

    // 接尾辞付きで作られたページは、元の持ち主が消えた後の再同期でも素の slug へ動かない（URL を壊さない）。
    [Fact]
    public async Task 接尾辞付きのslugは再同期で保たれる()
    {
        await using var fx = new Fixture();
        await fx.HandleAsync(Event(DocA, "週次の市況"));
        await fx.HandleAsync(Event(DocB, "週次の市況"));
        var suffixed = fx.Pages().Single(p => p.DocumentId == DocB).Slug;
        fx.RemovePage(DocA);

        await fx.HandleAsync(Event(DocB, "週次の市況"));

        fx.Pages().Single(p => p.DocumentId == DocB).Slug.Should().Be(suffixed);
    }

    // 題名が変われば、新しい題名で slug を取り直す（従来どおり）。新しい題名が他の文書と重なれば接尾辞が付く。
    [Fact]
    public async Task 題名が変われば新しい題名でslugを取り直す()
    {
        await using var fx = new Fixture();
        await fx.HandleAsync(Event(DocA, "障害報告"));
        await fx.HandleAsync(Event(DocB, "旧い題名"));

        await fx.HandleAsync(Event(DocB, "障害報告"));

        var pages = fx.Pages();
        pages.Single(p => p.DocumentId == DocA).Slug.Should().Be("障害報告");
        pages.Single(p => p.DocumentId == DocB).Slug.Should().StartWith("障害報告-").And.Contain(DocB.ToString("N")[..8]);
    }

    [Fact]
    public async Task 記号だけの題名は文書ID由来のslugになる()
    {
        await using var fx = new Fixture();

        await fx.HandleAsync(Event(DocA, "★★★"));
        await fx.HandleAsync(Event(DocB, "★★★"));

        var slugs = fx.Pages().Select(p => p.Slug).ToList();
        slugs.Should().OnlyHaveUniqueItems().And.NotContain(string.Empty);
        slugs.Should().Contain($"doc-{DocA:N}"[..12]);
    }

    // 長い題名でも、接尾辞付きの slug は列の長さ（500）に収まり、文書 ID を丸ごと含む末尾の候補は文書ごとに異なる。
    [Fact]
    public void slugの候補は列の長さに収まり末尾は文書IDを丸ごと含む()
    {
        var title = new string('あ', 500);

        var a = WikiPage.SlugCandidates(title, DocA);
        var b = WikiPage.SlugCandidates(title, DocB);

        a.Should().AllSatisfy(c => c.Length.Should().BeLessThanOrEqualTo(WikiPage.SlugMaxLength));
        a[^1].Should().EndWith(DocA.ToString("N"));
        a[^1].Should().NotBe(b[^1]);
        a[0].Should().Be(b[0], "題名だけの候補は同じ —— だから一意にならない");
    }

    // ---- T-26: 保存時の競合 ----

    [Fact]
    public async Task 保存時のslug競合は文書ID全桁へ付け替えて保存し直す()
    {
        await using var fx = new Fixture(call => call == 1 ? SlugViolation() : null);

        await fx.HandleAsync(Event(DocA, "同時に来た見出し"));

        fx.Pages().Should().ContainSingle().Which.Slug.Should().Be($"同時に来た見出し-{DocA:N}");
        fx.Saves.Calls.Should().Be(2);
        fx.Counted.Should().BeEmpty();
    }

    [Fact]
    public async Task 付け替えても衝突すれば記録してackする()
    {
        await using var fx = new Fixture(_ => SlugViolation());

        // 例外を上げない（Wolverine はメッセージを ack する）。
        await fx.HandleAsync(Event(DocA, "同時に来た見出し"));

        fx.Saves.Calls.Should().Be(2, "付け替えて 1 度だけ保存し直す");
        fx.Counted.Should().Equal((1L, WikiSyncSkippedMetrics.SlugConflict));
        fx.Logger.Warnings.Should().ContainSingle().Which.Should().Contain(DocA.ToString()).And.Contain("IX_Pages_Slug");
        fx.Pages().Should().BeEmpty();
    }

    public static TheoryData<string> TransientSaveFailures => ["document-id-unique", "connection"];

    // 🔴 同じ文書の並行作成（IX_Pages_DocumentId の 23505）と接続断は、恒久的な失敗ではない（次の試行で直る）。
    [Theory]
    [MemberData(nameof(TransientSaveFailures))]
    public async Task 文書IDの一意違反と接続断は例外のまま上げる(string kind)
    {
        Exception thrown = kind == "document-id-unique"
            ? new DbUpdateException("save failed", PgUniqueViolation("IX_Pages_DocumentId"))
            : new NpgsqlException("Failed to connect to wiki-db:5432", new SocketException((int)SocketError.ConnectionRefused));
        await using var fx = new Fixture(_ => thrown);

        var act = () => fx.HandleAsync(Event(DocA, "見出し"));

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(thrown);
        fx.Saves.Calls.Should().Be(1, "付け替えて保存し直すのは slug の一意違反だけ");
        fx.Counted.Should().BeEmpty();
    }

    // 本番の索引名と、受け口が突き合わせる制約名が一致する（索引名を変えると判定が黙って外れる）。
    [Fact]
    public void slugの一意索引の名前は受け口の判定と一致する()
    {
        using var db = new WikiDbContext(new DbContextOptionsBuilder<WikiDbContext>()
            .UseNpgsql("Host=localhost;Database=unused").Options);

        var index = db.Model.FindEntityType(typeof(WikiPage))!.GetIndexes()
            .Single(i => i.Properties.Single().Name == nameof(WikiPage.Slug));

        index.IsUnique.Should().BeTrue();
        index.GetDatabaseName().Should().Be(WikiDbContext.SlugIndexName).And.Be("IX_Pages_Slug");
    }

    // ---- T-27: 本文の欠落の分類 ----

    public static TheoryData<string> MissingKinds => ["typed", "error-code"];

    [Theory]
    [MemberData(nameof(MissingKinds))]
    public async Task NoSuchKeyは本文の欠落として写す(string kind)
    {
        AmazonS3Exception thrown = kind == "typed"
            ? new NoSuchKeyException("The specified key does not exist.") { StatusCode = HttpStatusCode.NotFound, ErrorCode = "NoSuchKey" }
            : new AmazonS3Exception("The specified key does not exist.") { StatusCode = HttpStatusCode.NotFound, ErrorCode = "NoSuchKey" };
        var reader = Reader(new ScriptedStorage(_ => throw thrown));

        var act = () => reader.ReadAsync(BodyUri, "t", Ct);

        var missing = (await act.Should().ThrowAsync<WikiBodyMissingException>()).Which;
        missing.MarkdownUri.Should().Be(BodyUri);
        missing.InnerException.Should().BeSameAs(thrown);
    }

    public static TheoryData<string> TransientReadKinds => ["no-such-bucket-404", "internal-error-500", "slow-down-503", "timeout", "connection"];

    // 🔴 一時障害・構成の不調は欠落へ写さない（再試行へ委ねる）。404 でも `NoSuchBucket` は欠落ではない。
    [Theory]
    [MemberData(nameof(TransientReadKinds))]
    public async Task 一時障害は本文の欠落へ写さない(string kind)
    {
        Exception thrown = kind switch
        {
            "no-such-bucket-404" => new AmazonS3Exception("The specified bucket does not exist")
            { StatusCode = HttpStatusCode.NotFound, ErrorCode = "NoSuchBucket" },
            "internal-error-500" => new AmazonS3Exception("We encountered an internal error.")
            { StatusCode = HttpStatusCode.InternalServerError, ErrorCode = "InternalError" },
            "slow-down-503" => new AmazonS3Exception("Please reduce your request rate.")
            { StatusCode = HttpStatusCode.ServiceUnavailable, ErrorCode = "SlowDown" },
            "timeout" => new TimeoutException("The operation has timed out."),
            _ => new HttpRequestException("Connection refused (seaweedfs-s3:8333)"),
        };
        var reader = Reader(new ScriptedStorage(_ => throw thrown));

        var act = () => reader.ReadAsync(BodyUri, "t", Ct);

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(thrown);
    }

    // ---- T-28: 本文の欠落の受け口 ----

    [Fact]
    public async Task 本文が無ければ記録だけしてackする()
    {
        await using var fx = new Fixture(reader: Reader(new ScriptedStorage(_ => throw new NoSuchKeyException("missing"))));

        await fx.HandleAsync(Event(DocA, "切り替え前のニュース", BodyUri));

        fx.Counted.Should().Equal((1L, WikiSyncSkippedMetrics.BodyMissing));
        fx.Logger.Warnings.Should().ContainSingle().Which.Should()
            .Contain(DocA.ToString()).And.Contain(BodyUri).And.Contain("2026-09-20T03:04:05").And.Contain("NoSuchKey");
        fx.Pages().Should().BeEmpty("メタデータを書かない");
        fx.Wiki.Pushed.Should().BeEmpty("Wiki.js へも書かない");
        fx.Saves.Calls.Should().Be(0);
    }

    // 対照: 本文の一時障害は受け口でも例外のまま。
    [Fact]
    public async Task 本文の一時障害は受け口でも例外のまま上げる()
    {
        await using var fx = new Fixture(reader: Reader(new ScriptedStorage(_ => throw new AmazonS3Exception("internal")
        { StatusCode = HttpStatusCode.InternalServerError, ErrorCode = "InternalError" })));

        var act = () => fx.HandleAsync(Event(DocA, "見出し", BodyUri));

        await act.Should().ThrowAsync<AmazonS3Exception>();
        fx.Counted.Should().BeEmpty();
    }

    // ---- T-29: 実配送（Wolverine・本番と同じ共通既定） ----

    public static TheoryData<string> PermanentKinds => ["slug-conflict", "body-missing", "value-too-long"];

    [Theory]
    [MemberData(nameof(PermanentKinds))]
    public async Task 実配送で恒久的な失敗は一回の試行で終わりDLQへ行かない(string kind)
    {
        var storage = new ScriptedStorage(_ => kind == "body-missing"
            ? throw new NoSuchKeyException("The specified key does not exist.")
            : "# 本文");
        var saves = new ScriptedSaveFailures(_ => kind == "slug-conflict" ? SlugViolation() : null);
        var wiki = new RecordingWiki(_ => kind == "value-too-long" ? ValueTooLong() : null);
        using var host = BuildHost(storage, saves, wiki);
        await host.StartAsync(Ct);

        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(TimeSpan.FromSeconds(20))
            .PublishMessageAndWaitAsync(Event(DocA, "同名の見出し", BodyUri));
        await host.StopAsync(Ct);

        session.MovedToErrorQueue.Envelopes().Should().BeEmpty("恒久的な失敗は DLQ へ送らない");
        session.MessageSucceeded.SingleMessage<DocumentUpdated>().DocumentId.Should().Be(DocA);
        storage.Calls.Should().Be(1, "再試行しない");
        saves.Calls.Should().Be(kind == "slug-conflict" ? 2 : 0, "受け口の中の付け替えは 1 度だけ");
        wiki.UpsertCalls.Should().Be(kind == "body-missing" ? 0 : 1, "Wiki.js へも 1 度だけ（再試行しない）");
    }

    [Fact]
    public async Task 実配送でDB接続断は再試行される()
    {
        var storage = new ScriptedStorage(_ => "# 本文");
        var saves = new ScriptedSaveFailures(call => call == 1
            ? new NpgsqlException("Failed to connect to wiki-db:5432", new SocketException((int)SocketError.ConnectionRefused))
            : null);
        var wiki = new RecordingWiki();
        using var host = BuildHost(storage, saves, wiki);
        await host.StartAsync(Ct);

        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(TimeSpan.FromSeconds(30))
            .PublishMessageAndWaitAsync(Event(DocA, "見出し", BodyUri));
        await host.StopAsync(Ct);

        session.MovedToErrorQueue.Envelopes().Should().BeEmpty();
        saves.Calls.Should().Be(2, "1 回目は接続断、再試行の 2 回目で保存する");
        storage.Calls.Should().Be(2);
    }

    // ---- T-30〜T-34: 表題の長さ（#1966・[[IADR-0554]]） ----

    [Fact]
    public async Task 表題が255文字ちょうどなら切らずに送る()
    {
        await using var fx = new Fixture();
        var title = new string('あ', WikiJsColumns.TitleMaxLength);

        await fx.HandleAsync(Event(DocA, title));

        fx.Wiki.Pushed.Should().ContainSingle().Which.Title.Should().BeSameAs(title);
        fx.Truncated.Should().BeEmpty("上限ちょうどは切らない");
        fx.Pages().Single().Title.Should().Be(title);
    }

    [Fact]
    public async Task 表題が256文字なら255文字へ切り詰め印を付けて数える()
    {
        await using var fx = new Fixture();
        var title = new string('a', 200) + new string('ニ', 56);

        await fx.HandleAsync(Event(DocA, title));

        var sent = fx.Wiki.Pushed.Should().ContainSingle().Which.Title;
        WikiText.CountChars(sent).Should().Be(WikiJsColumns.TitleMaxLength);
        sent.Should().Be(title[..254] + WikiText.Ellipsis);
        fx.Truncated.Should().Equal((1L, WikiSyncSkippedMetrics.TitleField));
        fx.Counted.Should().BeEmpty("切り詰めは skipped ではない（同期は成功している）");
        fx.Pages().Single().Title.Should().Be(title, "台帳の表題（500 まで）は元のまま残す");
    }

    // 台帳の表題（varchar(500)）と slug（varchar(500)）も、500 を超える表題で保存の 22001 にならない。
    [Fact]
    public async Task 台帳の表題とslugの候補は列の長さに収まる()
    {
        await using var fx = new Fixture();
        var title = string.Concat(Enumerable.Repeat("long-headline ", 60)); // 840 文字

        await fx.HandleAsync(Event(DocA, title));

        var page = fx.Pages().Single();
        WikiText.CountChars(page.Title).Should().Be(WikiPage.TitleMaxLength);
        page.Title.Should().EndWith(WikiText.Ellipsis);
        WikiPage.SlugCandidates(title, DocA).Should()
            .AllSatisfy(c => c.Length.Should().BeLessThanOrEqualTo(WikiPage.SlugMaxLength))
            .And.AllSatisfy(c => c.Should().NotEndWith("-").And.NotContain(WikiText.Ellipsis));
        page.Slug.Length.Should().BeLessThanOrEqualTo(WikiPage.SlugMaxLength);
        // 説明は空の定数、パスは doc/<GUID> の 40 文字固定で、Wiki.js の列の長さ（255）に収まる（切り詰め不要の根拠）。
        WikiPage.PathFor(DocA).Length.Should().Be(40).And.BeLessThanOrEqualTo(WikiJsColumns.PathMaxLength);
    }

    // 切り詰めの導入で、上限に届かない題名の slug（既存の URL）が変わらない。末尾の `-` を落とすのは切ったときだけ。
    [Theory]
    [InlineData("経費精算規程", "経費精算規程")]
    [InlineData("Hello, World! ", "hello-world")]
    [InlineData("見出し！", "見出し")]
    [InlineData("--前後の記号--", "前後の記号")]
    public void 切らない題名のslugは従来と同じ(string title, string expected)
    {
        WikiPage.SlugCandidates(title, DocA)[0].Should().Be(expected);
    }

    [Fact]
    public async Task Wikijsが長さで拒めば記録してackする()
    {
        await using var fx = new Fixture(wiki: new RecordingWiki(_ => ValueTooLong()));

        // 例外を上げない（Wolverine はメッセージを ack する）。
        await fx.HandleAsync(Event(DocA, "長いタグを持つ文書"));

        fx.Counted.Should().Equal((1L, WikiSyncSkippedMetrics.ValueTooLong));
        fx.Logger.Warnings.Should().ContainSingle().Which.Should()
            .Contain(DocA.ToString()).And.Contain("value is too long").And.Contain("character varying(255)")
            .And.NotContain("長いタグを持つ文書", "機密文書の題名をログへ出さない");
        fx.Pages().Should().BeEmpty("メタデータを書かない");
        fx.Saves.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Wikijsの一時障害は例外のまま上げる()
    {
        var thrown = new WikiJsSyncException("Wiki.js pages.create failed for 'doc/x' (code=1): connect ECONNREFUSED 10.0.0.7:5432");
        await using var fx = new Fixture(wiki: new RecordingWiki(_ => thrown));

        var act = () => fx.HandleAsync(Event(DocA, "見出し"));

        (await act.Should().ThrowAsync<WikiJsSyncException>()).Which.Should().BeSameAs(thrown);
        fx.Counted.Should().BeEmpty();
    }

    [Fact]
    public async Task 実配送でWikijsの一時障害は再試行される()
    {
        var storage = new ScriptedStorage(_ => "# 本文");
        var saves = new ScriptedSaveFailures(_ => null);
        var wiki = new RecordingWiki(call => call == 1 ? new WikiJsSyncException("Wiki.js GraphQL error: boom") : null);
        using var host = BuildHost(storage, saves, wiki);
        await host.StartAsync(Ct);

        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(TimeSpan.FromSeconds(30))
            .PublishMessageAndWaitAsync(Event(DocA, "見出し", BodyUri));
        await host.StopAsync(Ct);

        session.MovedToErrorQueue.Envelopes().Should().BeEmpty();
        wiki.UpsertCalls.Should().Be(2, "1 回目は一時障害、再試行の 2 回目で反映する");
        saves.Calls.Should().Be(1);
    }

    // ---- 器 ----

    // 実測（2026-10-16・#1966）の Wiki.js の応答をクライアントが写した形。
    private static Exception ValueTooLong()
        => new WikiJsInputRejectedException("create", $"doc/{DocA}", "1",
            "insert into \"pages\" (\"authorId\", ...) values ($1, ...) returning \"id\" - value too long for type character varying(255)");

    private static Exception SlugViolation()
        => new DbUpdateException("An error occurred while saving the entity changes.", PgUniqueViolation("IX_Pages_Slug"));

    private static PostgresException PgUniqueViolation(string constraint)
        => new(messageText: $"duplicate key value violates unique constraint \"{constraint}\"",
            severity: "ERROR", invariantSeverity: "ERROR", sqlState: PostgresErrorCodes.UniqueViolation,
            constraintName: constraint);

    private static StorageMarkdownReader Reader(IObjectStorageClient storage) =>
        new(new HttpClient(), storage, NullLogger<StorageMarkdownReader>.Instance);

    private static IHost BuildHost(ScriptedStorage storage, ScriptedSaveFailures saves, RecordingWiki wiki)
    {
        var dbName = $"wiki-1934-{Guid.NewGuid()}";
        return new HostBuilder()
            .UseWolverine(opts =>
            {
                opts.AddPlatformWolverineStep<DocumentSyncConsumer>(new PipelineOptions());
                // 本番（Program.cs）と同じ再試行・DLQ の既定を必ず通す。
                opts.UsePlatformMessagingDefaults();
                opts.PublishMessage<DocumentUpdated>().ToLocalQueue("wiki-sync-permanent-probe");
            })
            .ConfigureServices(services => services
                .AddLogging()
                .AddPlatformConsumerTimeouts()
                .AddSingleton(WikiSyncTimeouts.Default)
                .AddMetrics()
                .AddSingleton<WikiSyncSkippedMetrics>()
                .AddSingleton(saves)
                .AddDbContext<WikiDbContext>((sp, o) => o.UseInMemoryDatabase(dbName)
                    .AddInterceptors(sp.GetRequiredService<ScriptedSaveFailures>()))
                .AddSingleton<IWikiJsClient>(wiki)
                .AddSingleton<IWikiContentReader>(Reader(storage))
                .DisableAllExternalWolverineTransports())
            .Build();
    }

    // 受け口を直接呼ぶ器（1 通ごとに新しいスコープ。本番の配信単位と同じ）。
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly TestMeterFactory _meters = new();
        private readonly MeterListener _listener;
        private readonly List<(long, string)> _counted = [];
        private readonly List<(long, string)> _truncated = [];

        public Fixture(Func<int, Exception?>? saveFailure = null, IWikiContentReader? reader = null, RecordingWiki? wiki = null)
        {
            Wiki = wiki ?? new RecordingWiki();
            Saves = new ScriptedSaveFailures(saveFailure ?? (_ => null));
            var dbName = $"wiki-1934-{Guid.NewGuid()}";
            _provider = new ServiceCollection()
                .AddLogging()
                .AddPlatformConsumerTimeouts()
                .AddSingleton(WikiSyncTimeouts.Default)
                .AddSingleton(Saves)
                .AddDbContext<WikiDbContext>((sp, o) => o.UseInMemoryDatabase(dbName)
                    .AddInterceptors(sp.GetRequiredService<ScriptedSaveFailures>()))
                .AddSingleton<IWikiJsClient>(Wiki)
                .AddSingleton(reader ?? Reader(new ScriptedStorage(_ => "# 本文")))
                .BuildServiceProvider(true);
            Metrics = new WikiSyncSkippedMetrics(_meters);
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Scope == _meters
                        && instrument.Name is WikiSyncSkippedMetrics.CounterName or WikiSyncSkippedMetrics.TruncatedCounterName)
                        l.EnableMeasurementEvents(instrument);
                },
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                if (instrument.Name == WikiSyncSkippedMetrics.TruncatedCounterName)
                {
                    string field = "";
                    foreach (var tag in tags)
                        if (tag.Key == WikiSyncSkippedMetrics.FieldTag) field = tag.Value as string ?? "";
                    lock (_counted) _truncated.Add((value, field));
                    return;
                }
                string reason = "";
                foreach (var tag in tags)
                    if (tag.Key == WikiSyncSkippedMetrics.ReasonTag) reason = tag.Value as string ?? "";
                lock (_counted) _counted.Add((value, reason));
            });
            _listener.Start();
        }

        public ScriptedSaveFailures Saves { get; }
        public RecordingWiki Wiki { get; }
        public WarningLogger Logger { get; } = new();
        public WikiSyncSkippedMetrics Metrics { get; }
        public IReadOnlyList<(long, string)> Counted { get { lock (_counted) return _counted.ToArray(); } }
        public IReadOnlyList<(long, string)> Truncated { get { lock (_counted) return _truncated.ToArray(); } }

        public async Task HandleAsync(DocumentUpdated ev)
        {
            using var scope = _provider.CreateScope();
            var consumer = new DocumentSyncConsumer(
                scope.ServiceProvider.GetRequiredService<WikiDbContext>(),
                Wiki,
                scope.ServiceProvider.GetRequiredService<IWikiContentReader>(),
                scope.ServiceProvider.GetRequiredService<ConsumerCallTimeouts>(),
                scope.ServiceProvider.GetRequiredService<WikiSyncTimeouts>(),
                Metrics,
                Logger);
            await consumer.Handle(ev, Ct);
        }

        public List<WikiPage> Pages()
        {
            using var scope = _provider.CreateScope();
            return scope.ServiceProvider.GetRequiredService<WikiDbContext>().Pages.AsNoTracking().ToList();
        }

        public void RemovePage(Guid documentId)
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
            db.Pages.Remove(db.Pages.Single(p => p.DocumentId == documentId));
            Saves.Paused = true;
            db.SaveChanges();
            Saves.Paused = false;
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Dispose();
            _meters.Dispose();
            await _provider.DisposeAsync();
        }
    }

    // 保存の N 回目に例外を投げる割り込み（InMemory は一意索引を強制しないので、23505 はここで再現する）。
    internal sealed class ScriptedSaveFailures(Func<int, Exception?> onSave) : SaveChangesInterceptor
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public bool Paused { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Paused && onSave(Interlocked.Increment(ref _calls)) is { } ex) throw ex;
            return ValueTask.FromResult(result);
        }
    }

    internal sealed class ScriptedStorage(Func<int, string> onGet) : IObjectStorageClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<string> GetTextAsync(string uri, CancellationToken ct = default)
            => Task.FromResult(onGet(Interlocked.Increment(ref _calls)));

        public bool CanResolve(string? uri) => uri?.StartsWith("storage://", StringComparison.Ordinal) == true;

        public Task<string> PutTextAsync(string key, string text, string contentType, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<string> PutBytesAsync(string key, byte[] bytes, string contentType, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<byte[]> GetBytesAsync(string uri, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string uri, CancellationToken ct = default) => throw new NotSupportedException();
        public string CreatePresignedGetUrl(string uri, TimeSpan? expiry = null) => throw new NotSupportedException();
    }

    // onUpsert: N 回目（1 始まり）の Upsert に投げる例外（null は成功）。#1966 の Wiki.js の失敗の再現に使う。
    internal sealed class RecordingWiki(Func<int, Exception?>? onUpsert = null) : IWikiJsClient
    {
        private readonly List<WikiJsPage> _pushed = [];
        private int _calls;
        public IReadOnlyList<WikiJsPage> Pushed { get { lock (_pushed) return _pushed.ToArray(); } }
        public int UpsertCalls => Volatile.Read(ref _calls);

        public Task UpsertPageAsync(WikiJsPage page, CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (onUpsert?.Invoke(call) is { } ex) throw ex;
            lock (_pushed) _pushed.Add(page);
            return Task.CompletedTask;
        }

        public Task ArchivePageAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeletePageAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetRenderedContentAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    internal sealed class WarningLogger : ILogger<DocumentSyncConsumer>
    {
        private readonly List<string> _warnings = [];
        public IReadOnlyList<string> Warnings { get { lock (_warnings) return _warnings.ToArray(); } }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) lock (_warnings) _warnings.Add(formatter(state, exception));
        }
    }
}
