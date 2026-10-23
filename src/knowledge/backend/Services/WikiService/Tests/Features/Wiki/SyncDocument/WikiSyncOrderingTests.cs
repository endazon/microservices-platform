using System.Collections.Concurrent;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Messaging;
using WikiService.Domain;
using WikiService.Domain.Ports;
using WikiService.Features.Wiki;
using WikiService.Features.Wiki.RemoveDeleted;
using WikiService.Features.Wiki.SyncDocument;
using WikiService.Infrastructure.Persistence;

namespace WikiService.Tests.Features.Wiki.SyncDocument;

// FR-13, FR-06, ADR-0027, [[IADR-0596]] (#2102): Wiki の受け手の順序。
// `DocumentUpdated` / `DocumentDeleted` は少なくとも 1 回届き、順序は保証されない（再配送・デッドレターからの再投入・2 レプリカ・
// 更新と削除の 2 本のリスナー）。古い版が新しい版を上書きしないこと、撤去・アーカイブ・削除の後に古い版で復活しないこと、
// 並行して保存で負けた古い版が Wiki.js に残らないことを固定する。
//
// Wiki.js は「パスごとの今の状態」を持つ偽物で測る（push・アーカイブ・削除の順に上書きされる）。
[Trait("TestKind", "Unit")]
public class WikiSyncOrderingTests
{
    private static readonly Guid DocId = Guid.Parse("21022102-2102-2102-2102-210221022102");
    private static readonly string PathOfDoc = WikiPage.PathFor(DocId);
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddMinutes(1);
    private static readonly DateTimeOffset T2 = T0.AddMinutes(2);
    private static readonly DateTimeOffset T3 = T0.AddMinutes(3);
    private static readonly DateTimeOffset T4 = T0.AddMinutes(4);

    private static ServiceProvider BuildProvider()
    {
        var dbName = $"wiki-ordering-{Guid.NewGuid()}";
        return new ServiceCollection()
            .AddLogging()
            .AddPlatformConsumerTimeouts()
            .AddSingleton(WikiSyncTimeouts.Default)
            // 並行の試験は 1 つの受け口の Wiki.js 呼び出しの中で別の受け口を走らせるので、門の枠を 2 にする（1 だと互いを待つ）。
            .AddSingleton(new WikiJsWriteGate(2))
            .AddDbContext<WikiDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddSingleton<StatefulWikiJs>()
            .AddSingleton<IWikiJsClient>(sp => sp.GetRequiredService<StatefulWikiJs>())
            .AddSingleton<CountingContentReader>()
            .AddSingleton<IWikiContentReader>(sp => sp.GetRequiredService<CountingContentReader>())
            .BuildServiceProvider(true);
    }

    // 1 通ごとに新しいスコープ（本番の配信単位と同じ）。
    private static async Task SyncAsync(ServiceProvider provider, DocumentUpdated ev)
    {
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;
        await new DocumentSyncConsumer(
                sp.GetRequiredService<WikiDbContext>(),
                sp.GetRequiredService<IWikiJsClient>(),
                sp.GetRequiredService<IWikiContentReader>(),
                sp.GetRequiredService<ConsumerCallTimeouts>(),
                sp.GetRequiredService<WikiSyncTimeouts>(),
                sp.GetRequiredService<WikiJsWriteGate>(),
                TestMeterFactory.NewSkippedMetrics(),
                sp.GetRequiredService<ILogger<DocumentSyncConsumer>>())
            .Handle(ev, TestContext.Current.CancellationToken);
    }

    private static async Task DeleteAsync(ServiceProvider provider, DateTimeOffset deletedAt)
    {
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;
        await new DocumentDeletedConsumer(
                sp.GetRequiredService<WikiDbContext>(),
                sp.GetRequiredService<IWikiJsClient>(),
                sp.GetRequiredService<IWikiContentReader>(),
                sp.GetRequiredService<ConsumerCallTimeouts>(),
                sp.GetRequiredService<WikiSyncTimeouts>(),
                sp.GetRequiredService<WikiJsWriteGate>(),
                sp.GetRequiredService<ILogger<DocumentDeletedConsumer>>())
            .Handle(new DocumentDeleted(DocId, deletedAt), TestContext.Current.CancellationToken);
    }

    private static DocumentUpdated Updated(string title, DateTimeOffset at, string confidentiality = "internal",
        string status = "published", Dictionary<string, string>? extra = null)
    {
        var attributes = new Dictionary<string, string> { ["confidentiality"] = confidentiality };
        foreach (var (k, v) in extra ?? []) attributes[k] = v;
        return new(DocId, title, status, $"storage://b/{DocId:N}/document.md", attributes, ["ops"], at);
    }

    private static WikiPage? PageOf(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<WikiDbContext>().Pages.AsNoTracking()
            .FirstOrDefault(p => p.DocumentId == DocId);
    }

    private static int PageRows(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<WikiDbContext>().Pages.Count(p => p.DocumentId == DocId);
    }

    // 露出の 3 属性を 3 つとも外した組織文書（露出の門で Wiki.js から撤去される）。
    private static Dictionary<string, string> Excluded()
        => DocumentExposure.Project(includeInSearch: false, includeInGraph: false, includeInAi: false);

    // 受け入れ基準 1・2: 新しい版（confidential へ厳格化）の後に古い版（public）が届いても、Wiki.js もメタデータも新しい版のまま。
    [Fact]
    public async Task 保持中より古い更新は反映されない_厳格化後に公開へ戻らない()
    {
        await using var provider = BuildProvider();
        var wiki = provider.GetRequiredService<StatefulWikiJs>();
        var reader = provider.GetRequiredService<CountingContentReader>();

        await SyncAsync(provider, Updated("新しい版", T2, confidentiality: "confidential"));
        var readsAfterNew = reader.Reads;

        await SyncAsync(provider, Updated("古い版", T1, confidentiality: "public"));

        wiki.StateOf(PathOfDoc).Should().BeEquivalentTo(new WikiJsState(WikiJsKind.Page, "新しい版", IsPrivate: true));
        var page = PageOf(provider)!;
        page.Title.Should().Be("新しい版");
        page.Attributes["confidentiality"].Should().Be("confidential");
        // 古い版は本文を読まず、Wiki.js へも書かない。
        reader.Reads.Should().Be(readsAfterNew);
        wiki.Writes.Should().Be(1);
    }

    // 受け入れ基準 6: 同じ版の再配送は弾かず、同じ内容に収束する（行は 1 つ）。
    [Fact]
    public async Task 同じ版の再配送は収束する()
    {
        await using var provider = BuildProvider();
        var wiki = provider.GetRequiredService<StatefulWikiJs>();

        await SyncAsync(provider, Updated("版", T1));
        await SyncAsync(provider, Updated("版", T1));

        wiki.Writes.Should().Be(2);
        wiki.StateOf(PathOfDoc).Should().BeEquivalentTo(new WikiJsState(WikiJsKind.Page, "版", IsPrivate: true));
        PageRows(provider).Should().Be(1);
    }

    // 受け入れ基準 3: 削除の後に古い版が届いても Wiki.js にもメタデータにも書き戻さない。
    [Fact]
    public async Task 削除の後に古い版が届いても書き戻されない()
    {
        await using var provider = BuildProvider();
        var wiki = provider.GetRequiredService<StatefulWikiJs>();

        await SyncAsync(provider, Updated("版", T1));
        wiki.StateOf(PathOfDoc).Kind.Should().Be(WikiJsKind.Page); // 陽性対照: 削除の前は載っている
        await DeleteAsync(provider, T3);

        await SyncAsync(provider, Updated("削除より前の版", T2));
        // 削除と同じ時刻の更新も古い（削除は同じ文書の最後の出来事）。
        await SyncAsync(provider, Updated("削除と同じ時刻の版", T3));

        wiki.StateOf(PathOfDoc).Kind.Should().Be(WikiJsKind.Absent);
        PageOf(provider).Should().BeNull();
    }

    // 受け入れ基準 3（陽性対照）: 決定的な文書 ID で作り直された文書（削除より新しい版）は載る。
    [Fact]
    public async Task 削除より新しい版は作り直される()
    {
        await using var provider = BuildProvider();
        var wiki = provider.GetRequiredService<StatefulWikiJs>();

        await SyncAsync(provider, Updated("版", T1));
        await DeleteAsync(provider, T2);
        await SyncAsync(provider, Updated("作り直した版", T3));

        wiki.StateOf(PathOfDoc).Should().BeEquivalentTo(new WikiJsState(WikiJsKind.Page, "作り直した版", IsPrivate: true));
        PageOf(provider)!.Title.Should().Be("作り直した版");
    }

    // 受け入れ基準 5: 作り直した版を反映した後に遅れて届いた古い削除は、作り直したページを消さない。
    [Fact]
    public async Task 作り直した後に古い削除が届いても消さない()
    {
        await using var provider = BuildProvider();
        var wiki = provider.GetRequiredService<StatefulWikiJs>();

        await SyncAsync(provider, Updated("作り直した版", T3));
        await DeleteAsync(provider, T2);

        wiki.StateOf(PathOfDoc).Kind.Should().Be(WikiJsKind.Page);
        PageOf(provider).Should().NotBeNull();
    }

    // 受け入れ基準 4: 露出の門で撤去した後に、それより古い（露出を外す前の）版が届いても書き戻さない。
    [Fact]
    public async Task 撤去の後に古い版が届いても書き戻されない()
    {
        await using var provider = BuildProvider();
        var wiki = provider.GetRequiredService<StatefulWikiJs>();

        await SyncAsync(provider, Updated("版", T1));
        await SyncAsync(provider, Updated("版", T3, extra: Excluded()));
        wiki.StateOf(PathOfDoc).Kind.Should().Be(WikiJsKind.Absent); // 撤去が効いている

        await SyncAsync(provider, Updated("露出を外す前の版", T2));

        wiki.StateOf(PathOfDoc).Kind.Should().Be(WikiJsKind.Absent);
        PageOf(provider).Should().BeNull();
    }

    // 受け入れ基準 4: アーカイブを反映した後に、それより古い版でページが公開へ戻らない。
    [Fact]
    public async Task アーカイブの後に古い版が届いても公開へ戻らない()
    {
        await using var provider = BuildProvider();
        var wiki = provider.GetRequiredService<StatefulWikiJs>();

        await SyncAsync(provider, Updated("版", T1, confidentiality: "public"));
        await SyncAsync(provider, Updated("版", T3, confidentiality: "public", status: "archived"));

        await SyncAsync(provider, Updated("アーカイブより前の版", T2, confidentiality: "public"));

        wiki.StateOf(PathOfDoc).Kind.Should().Be(WikiJsKind.Archived);
        PageOf(provider)!.Status.Should().Be(WikiPageStatus.Archived);
    }

    // 受け入れ基準 7: 古い版（T1）の push の最中に新しい版（T2）が push と保存を終え、古い版の push が後に着く。
    // 古い版は保存の突き合わせで負け、保存済みの最新の状態へ Wiki.js を合わせ直す。
    [Fact]
    public async Task 並行して保存で負けた古い版は最新の状態へ合わせ直す()
    {
        await using var provider = BuildProvider();
        var wiki = provider.GetRequiredService<StatefulWikiJs>();
        await SyncAsync(provider, Updated("最初の版", T0));

        wiki.BeforeNextUpsert = () => SyncAsync(provider, Updated("新しい版", T2, confidentiality: "confidential"));
        await SyncAsync(provider, Updated("古い版", T1, confidentiality: "public"));

        wiki.StateOf(PathOfDoc).Should().BeEquivalentTo(new WikiJsState(WikiJsKind.Page, "新しい版", IsPrivate: true));
        var page = PageOf(provider)!;
        page.Title.Should().Be("新しい版");
        page.Attributes["confidentiality"].Should().Be("confidential");
    }

    // 受け入れ基準 7: 古い版の push の最中に削除（別のリスナー）が終わる。古い版は負けて、ページを消し直す。
    [Fact]
    public async Task 並行して削除に負けた古い版はページを消し直す()
    {
        await using var provider = BuildProvider();
        var wiki = provider.GetRequiredService<StatefulWikiJs>();
        await SyncAsync(provider, Updated("最初の版", T0));

        wiki.BeforeNextUpsert = () => DeleteAsync(provider, T3);
        await SyncAsync(provider, Updated("古い版", T1));

        wiki.StateOf(PathOfDoc).Kind.Should().Be(WikiJsKind.Absent);
        PageOf(provider).Should().BeNull();
    }

    // 対照: 新しい版（T2）の push の最中に古い版（T1）が保存を終えると、新しい版は負けるが古くなってはいない。
    // 投げて再試行へ委ね、次の試行で新しい版が載る（合わせ直しで ack しない）。
    [Fact]
    public async Task 並行して保存で負けた新しい版は投げて再試行で反映される()
    {
        await using var provider = BuildProvider();
        var wiki = provider.GetRequiredService<StatefulWikiJs>();
        await SyncAsync(provider, Updated("最初の版", T0));

        wiki.BeforeNextUpsert = () => SyncAsync(provider, Updated("古い版", T1, confidentiality: "public"));
        var first = () => SyncAsync(provider, Updated("新しい版", T2, confidentiality: "confidential"));
        await first.Should().ThrowAsync<DbUpdateConcurrencyException>();

        await SyncAsync(provider, Updated("新しい版", T2, confidentiality: "confidential")); // 再試行

        wiki.StateOf(PathOfDoc).Should().BeEquivalentTo(new WikiJsState(WikiJsKind.Page, "新しい版", IsPrivate: true));
        PageOf(provider)!.Title.Should().Be("新しい版");
    }

    // 判定の純関数（[[IADR-0596]] 決定 1・2）。比較は保存の精度（マイクロ秒）で行う。
    [Fact]
    public void 判定は保存の精度で比べ_削除と同じ時刻の更新は古い()
    {
        DocumentSyncOrdering.IsStaleUpdate(null, T1).Should().BeFalse();

        var mark = DocumentSyncMark.For(DocId);
        mark.RecordUpdated(T2.AddTicks(7)); // マイクロ秒未満は保存されない
        DocumentSyncOrdering.IsStaleUpdate(mark, T2.AddTicks(3)).Should().BeFalse(); // 同じ版の再配送（精度の内）
        DocumentSyncOrdering.IsStaleUpdate(mark, T2.AddTicks(-10)).Should().BeTrue(); // 1 マイクロ秒でも古い
        DocumentSyncOrdering.IsStaleDelete(mark, T2).Should().BeFalse();
        DocumentSyncOrdering.IsStaleDelete(mark, T1).Should().BeTrue();

        mark.RecordDeleted(T3);
        DocumentSyncOrdering.IsStaleUpdate(mark, T3).Should().BeTrue();
        DocumentSyncOrdering.IsStaleUpdate(mark, T4).Should().BeFalse();
        DocumentSyncOrdering.IsStaleDelete(mark, T3).Should().BeFalse(); // 削除の再配送は適用する
    }
}

internal enum WikiJsKind { Absent, Page, Archived }

internal sealed record WikiJsState(WikiJsKind Kind, string? Title, bool IsPrivate);

// Wiki.js の「パスごとの今の状態」を持つ偽物。`BeforeNextUpsert` は次の push の**前**に 1 度だけ走る
// （その中で別の受け口を最後まで走らせ、この push を後に着かせる）。
internal sealed class StatefulWikiJs : IWikiJsClient
{
    private readonly ConcurrentDictionary<string, WikiJsState> _state = new();
    private int _writes;

    public Func<Task>? BeforeNextUpsert { get; set; }

    public int Writes => _writes;

    public WikiJsState StateOf(string path)
        => _state.TryGetValue(path, out var s) ? s : new WikiJsState(WikiJsKind.Absent, null, true);

    public async Task UpsertPageAsync(WikiJsPage page, CancellationToken ct = default)
    {
        var before = BeforeNextUpsert;
        BeforeNextUpsert = null;
        if (before is not null) await before();
        Interlocked.Increment(ref _writes);
        _state[page.Path] = new WikiJsState(WikiJsKind.Page, page.Title, page.IsPrivate);
    }

    public Task ArchivePageAsync(string path, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _writes);
        if (_state.TryGetValue(path, out var s)) _state[path] = s with { Kind = WikiJsKind.Archived, IsPrivate = true };
        return Task.CompletedTask;
    }

    public Task DeletePageAsync(string path, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _writes);
        _state.TryRemove(path, out _);
        return Task.CompletedTask;
    }

    public Task<string?> GetRenderedContentAsync(string path, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}

internal sealed class CountingContentReader : IWikiContentReader
{
    private int _reads;
    public int Reads => _reads;

    public Task<string> ReadAsync(string? markdownUri, string title, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _reads);
        return Task.FromResult($"# {title}\n\n本文");
    }
}
