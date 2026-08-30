using AwesomeAssertions;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using WikiService.Features.Wiki;
using WikiService.Features.Wiki.RemoveDeleted;
using WikiService.Features.Wiki.SyncDocument;
using WikiService.Infrastructure.Persistence;
using WikiService.Domain.Ports;

namespace WikiService.Tests;

// FR-13, UC-07, IADR-0020, IADR-0021: 文書更新イベント → Wiki.js 同期（GraphQL push）＋ ABAC メタデータ upsert。
// 受け入れ基準②「更新で正規化 Markdown が Wiki.js に反映」をイベント駆動同期で担保することの検証。
//
// E3b: 購読は Wolverine 段になった。本ファイルは Handle を直接呼ぶ（測るのは同期の写像であって
// 配送ではない。登録経路は PipelineRecomposeTests が Wolverine ホストで持つ —— E1 変異 R の教訓）。
public class DocumentSyncConsumerTests
{
    private static readonly Guid DocId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ServiceProvider BuildProvider(string dbName)
        => new ServiceCollection()
            .AddLogging()
            .AddDbContext<WikiDbContext>(o => o.UseInMemoryDatabase(dbName))
            // IADR-0021: Wiki.js への push と本文取得を記録スタブへ差し替え、稼働 Wiki.js に依存させない。
            .AddSingleton<RecordingWikiJsClient>()
            .AddSingleton<IWikiJsClient>(sp => sp.GetRequiredService<RecordingWikiJsClient>())
            .AddSingleton<IWikiContentReader, StubContentReader>()
            .BuildServiceProvider(true);

    // E3b: Wolverine 段の Handle を直接呼ぶ。1 通ごとに新しいスコープ（本番の配信単位と同じ）。
    private static async Task HandleAsync(ServiceProvider provider, DocumentUpdated ev)
    {
        using var scope = provider.CreateScope();
        var consumer = new DocumentSyncConsumer(
            scope.ServiceProvider.GetRequiredService<WikiDbContext>(),
            scope.ServiceProvider.GetRequiredService<IWikiJsClient>(),
            scope.ServiceProvider.GetRequiredService<IWikiContentReader>(),
            scope.ServiceProvider.GetRequiredService<ILogger<DocumentSyncConsumer>>());
        await consumer.Handle(ev, TestContext.Current.CancellationToken);
    }

    private static WikiService.Domain.WikiPage? PageOf(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
        return db.Pages.FirstOrDefault(p => p.DocumentId == DocId);
    }

    private static DocumentUpdated Event(string title, string status,
        Dictionary<string, string>? attrs = null)
        => new(DocId, title, status, "s3://b/doc.md",
            attrs ?? new Dictionary<string, string> { ["confidentiality"] = "internal" },
            ["ops"], DateTimeOffset.UtcNow);

    // 正規化文書の受信で Wiki ページが作成され、属性が保持される。
    [Fact]
    public async Task Consumer_CreatesPage_OnNormalizedDocument()
    {
        await using var provider = BuildProvider($"wiki-sync-{Guid.NewGuid()}");
        await HandleAsync(provider, Event("入社手続き", "normalized"));

        var page = PageOf(provider);
        page.Should().NotBeNull();
        page!.Title.Should().Be("入社手続き");
        page.Attributes["confidentiality"].Should().Be("internal");

        // 受け入れ基準②: 正規化 Markdown が Wiki.js へ DocumentId 由来の安定パスで push される。
        var wiki = provider.GetRequiredService<RecordingWikiJsClient>();
        wiki.Pushed.Should().ContainSingle();
        wiki.Pushed[0].Path.Should().Be($"doc/{DocId}");
        wiki.Pushed[0].Title.Should().Be("入社手続き");
        // IADR-0021: 認可属性は Wiki.js へ持ち込まない（push 内容は本文・タイトル・タグに限定）。
        // 多層防御: confidentiality=internal は public 以外のため Wiki.js 上でも非公開（deny-closed）。
        wiki.Pushed[0].IsPrivate.Should().BeTrue();
    }

    // 多層防御（ADR-0011/IADR-0021）: confidentiality=public のみ Wiki.js 上で公開、以外は非公開。
    [Theory]
    [InlineData("public", false)]
    [InlineData("internal", true)]
    [InlineData("restricted", true)]
    public async Task Consumer_SetsWikiJsPrivacy_FromConfidentiality(string confidentiality, bool expectedPrivate)
    {
        await using var provider = BuildProvider($"wiki-sync-{Guid.NewGuid()}");
        await HandleAsync(provider, Event("規程", "published",
            new() { ["confidentiality"] = confidentiality }));

        var wiki = provider.GetRequiredService<RecordingWikiJsClient>();
        wiki.Pushed.Should().ContainSingle();
        wiki.Pushed[0].IsPrivate.Should().Be(expectedPrivate);
    }

    // 多層防御: confidentiality 属性が欠落する場合も安全側（非公開）に倒す（deny-closed）。
    [Fact]
    public async Task Consumer_SetsWikiJsPrivate_WhenConfidentialityMissing()
    {
        await using var provider = BuildProvider($"wiki-sync-{Guid.NewGuid()}");
        await HandleAsync(provider, Event("属性なし規程", "published", new Dictionary<string, string>()));

        var wiki = provider.GetRequiredService<RecordingWikiJsClient>();
        wiki.Pushed.Should().ContainSingle();
        wiki.Pushed[0].IsPrivate.Should().BeTrue();
    }

    // 未公開（draft 等）は同期しない。
    [Fact]
    public async Task Consumer_Ignores_NonPublishedStatus()
    {
        await using var provider = BuildProvider($"wiki-sync-{Guid.NewGuid()}");
        await HandleAsync(provider, Event("下書き", "draft"));

        PageOf(provider).Should().BeNull();
        // 未公開は Wiki.js へも反映しない。
        provider.GetRequiredService<RecordingWikiJsClient>().Pushed.Should().BeEmpty();
    }

    // 再同期で既存ページが更新される（タイトル・属性が反映される）。
    [Fact]
    public async Task Consumer_UpdatesExistingPage_OnResync()
    {
        await using var provider = BuildProvider($"wiki-sync-{Guid.NewGuid()}");
        await HandleAsync(provider, Event("旧タイトル", "published",
            new() { ["confidentiality"] = "internal" }));
        await HandleAsync(provider, Event("新タイトル", "published",
            new() { ["confidentiality"] = "public" }));

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
        var pages = db.Pages.Where(p => p.DocumentId == DocId).ToList();
        pages.Should().ContainSingle();
        pages[0].Title.Should().Be("新タイトル");
        pages[0].Attributes["confidentiality"].Should().Be("public");
    }

    // ---- FR-19, ADR-0046 D-01: 個人資料は Wiki.js の同期対象から外す -----------------------
    //
    // 🔴 **否定形テストには陽性対照を対で置いてある。** 「常に早期 return する実装」も
    // 「doc_scope を一切見ない実装」も、否定形（private-note が同期されない）だけなら通す。
    //
    // 🔴 **Consumer_SyncsDocument_WhenDocScopeMissing の役割は「診断」である。**
    // 除外条件を `doc_scope != "organization"` と書く誤りは、doc_scope を持たない
    // 既存 2,368 件のフィクスチャを巻き添えで落とす。**本テストだけが「doc_scope の欠落を
    // 個人資料と誤判定してはならない」という理由つきで落ちる。**

    // 個人資料は Wiki.js へ push されず、ABAC 同期メタデータも作られない。
    [Fact]
    public async Task Consumer_SkipsPrivateNote_NoPushAndNoMetadata()
    {
        await using var provider = BuildProvider($"wiki-sync-{Guid.NewGuid()}");
        await HandleAsync(provider, Event("個人資料", "published",
            new() { ["confidentiality"] = "restricted", ["doc_scope"] = "private-note" }));

        PageOf(provider).Should()
            .BeNull("ADR-0046 D-01: Wiki.js 上に個人資料のページは作られない");
        provider.GetRequiredService<RecordingWikiJsClient>().Pushed.Should().BeEmpty();
    }

    // 陽性対照: 組織文書は従来どおり同期される（除外が広がっていないこと）。
    [Fact]
    public async Task Consumer_SyncsOrganizationDocument()
    {
        await using var provider = BuildProvider($"wiki-sync-{Guid.NewGuid()}");
        await HandleAsync(provider, Event("組織文書", "published",
            new() { ["confidentiality"] = "internal", ["doc_scope"] = "organization" }));

        provider.GetRequiredService<RecordingWikiJsClient>().Pushed.Should().ContainSingle();
    }

    // 🔴 陽性対照（本命）: doc_scope を持たない文書は従来どおり同期される。
    // 実データ 2,368 件はこの形であり（ADR-0054 §結果 で遡及付与しないと裁定済み）、
    // 除外を「organization でない」と書くと本テストだけが落ちる。
    [Fact]
    public async Task Consumer_SyncsDocument_WhenDocScopeMissing()
    {
        await using var provider = BuildProvider($"wiki-sync-{Guid.NewGuid()}");
        // doc_scope を持たない＝新設前から在る既存文書の形。
        await HandleAsync(provider, Event("既存文書", "published",
            new() { ["confidentiality"] = "internal" }));

        provider.GetRequiredService<RecordingWikiJsClient>().Pushed.Should()
            .ContainSingle("doc_scope の欠落を個人資料と誤判定してはならない"
                         + "（実データ 2,368 件がこの形で、除外を否定で書くと全滅する）");
    }

    // 個人資料の再配信で状態が変わらない（冪等・スキップが積み上がらない）。
    [Fact]
    public async Task Consumer_SkipsPrivateNote_Idempotently_OnRedelivery()
    {
        await using var provider = BuildProvider($"wiki-sync-{Guid.NewGuid()}");
        var ev = Event("個人資料", "published",
            new() { ["confidentiality"] = "restricted", ["doc_scope"] = "private-note" });
        await HandleAsync(provider, ev);
        await HandleAsync(provider, ev);

        PageOf(provider).Should().BeNull();
        provider.GetRequiredService<RecordingWikiJsClient>().Pushed.Should().BeEmpty();
    }

    // 境界: 個人資料であってもアーカイブ伝播は通す（除外はアーカイブ分岐より後に置いてある）。
    // ページが無くても ArchivePageAsync は冪等であり、deny-closed の向きである。
    [Fact]
    public async Task Consumer_StillPropagatesArchive_ForPrivateNote()
    {
        await using var provider = BuildProvider($"wiki-sync-{Guid.NewGuid()}");
        await HandleAsync(provider, Event("個人資料", "archived",
            new() { ["confidentiality"] = "restricted", ["doc_scope"] = "private-note" }));

        // アーカイブ経路は push を行わない（非公開化のみ）。例外なく完走することを確かめる。
        provider.GetRequiredService<RecordingWikiJsClient>().Pushed.Should().BeEmpty();
    }

    // 🔴 現状の固定（仕様ではなく観測）: 組織文書として同期済みの文書が後から個人資料へ変わっても、
    // 既に作られた Wiki.js ページとメタデータは**残る**。
    //
    // ADR-0046 D-01 は「ページは作られない」と定めるが「既にあるページを消す」とは定めておらず、
    // doc_scope が文書の生涯で変わり得るのかも計画は述べていない。**実装で決めていない。**
    // 計画へ問い、裁定が出たら本テストを書き換える（作業仕様書 §5・§8）。
    [Fact]
    public async Task Consumer_LeavesExistingPage_WhenDocumentBecomesPrivateNote_CurrentBehaviour()
    {
        await using var provider = BuildProvider($"wiki-sync-{Guid.NewGuid()}");
        // 1 通目: 組織文書として同期される。
        await HandleAsync(provider, Event("組織文書", "published",
            new() { ["confidentiality"] = "internal", ["doc_scope"] = "organization" }));
        // 2 通目: 同一文書が個人資料へ変わる。
        await HandleAsync(provider, Event("個人資料になった", "published",
            new() { ["confidentiality"] = "restricted", ["doc_scope"] = "private-note" }));

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
        var page = db.Pages.Single(p => p.DocumentId == DocId);
        page.Title.Should().Be("組織文書", "以後は同期されないためタイトルは更新されない");
        // push は 1 通目の 1 回だけ。2 通目は除外される。
        provider.GetRequiredService<RecordingWikiJsClient>().Pushed.Should().ContainSingle();
    }
}

// IADR-0021: Wiki.js への push を記録するテスト用スタブ（同期の呼び出し・内容を検証する）。
file sealed class RecordingWikiJsClient : IWikiJsClient
{
    public ConcurrentQueue<WikiJsPage> PushedQueue { get; } = new();
    public IReadOnlyList<WikiJsPage> Pushed => PushedQueue.ToArray();

    public Task UpsertPageAsync(WikiJsPage page, CancellationToken ct = default)
    {
        PushedQueue.Enqueue(page);
        return Task.CompletedTask;
    }

    public Task ArchivePageAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeletePageAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

    public Task<string?> GetRenderedContentAsync(string path, CancellationToken ct = default)
        => Task.FromResult<string?>($"<article data-path=\"{path}\">rendered</article>");
}

// IADR-0021: 正規化 Markdown 本文取得のテスト用スタブ（本文はタイトルから生成）。
file sealed class StubContentReader : IWikiContentReader
{
    public Task<string> ReadAsync(string? markdownUri, string title, CancellationToken ct = default)
        => Task.FromResult($"# {title}\n\n本文");
}
