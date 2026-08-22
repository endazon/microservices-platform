using AwesomeAssertions;
using Knowledge.Contracts.Events;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using WikiService.Api.Composable.Steps;
using WikiService.Api.Foundation.Persistence;
using WikiService.Api.Foundation.Ports;
using WikiService.Api.Foundation.Services;

namespace WikiService.Api.Tests;

// FR-13, UC-07, IADR-0020, IADR-0021: 文書更新イベント → Wiki.js 同期（GraphQL push）＋ ABAC メタデータ upsert。
// 受け入れ基準②「更新で正規化 Markdown が Wiki.js に反映」をイベント駆動同期で担保することの検証。
public class DocumentSyncConsumerTests
{
    private static readonly Guid DocId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ServiceProvider BuildHarness(string dbName)
        => new ServiceCollection()
            .AddLogging()
            .AddDbContext<WikiDbContext>(o => o.UseInMemoryDatabase(dbName))
            // IADR-0021: Wiki.js への push と本文取得を記録スタブへ差し替え、稼働 Wiki.js に依存させない。
            .AddSingleton<RecordingWikiJsClient>()
            .AddSingleton<IWikiJsClient>(sp => sp.GetRequiredService<RecordingWikiJsClient>())
            .AddSingleton<IWikiContentReader, StubContentReader>()
            .AddMassTransitTestHarness(cfg => cfg.AddConsumer<DocumentSyncConsumer>())
            .BuildServiceProvider(true);

    // 同期は非同期に完了するため、期待状態になるまで短時間ポーリングする。
    private static async Task WaitForAsync(ServiceProvider provider,
        Func<WikiService.Api.Foundation.Domain.WikiPage?, bool> predicate)
    {
        for (var i = 0; i < 50; i++)
        {
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
                var page = db.Pages.FirstOrDefault(p => p.DocumentId == DocId);
                if (predicate(page)) return;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException("Wiki ページが期待状態になりませんでした。");
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
        var dbName = $"wiki-sync-{Guid.NewGuid()}";
        await using var provider = BuildHarness(dbName);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(Event("入社手続き", "normalized"), TestContext.Current.CancellationToken);
            (await harness.Consumed.Any<DocumentUpdated>(TestContext.Current.CancellationToken)).Should().BeTrue();

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
            var page = db.Pages.SingleOrDefault(p => p.DocumentId == DocId);
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
        finally { await harness.Stop(TestContext.Current.CancellationToken); }
    }

    // 多層防御（ADR-0011/IADR-0021）: confidentiality=public のみ Wiki.js 上で公開、以外は非公開。
    [Theory]
    [InlineData("public", false)]
    [InlineData("internal", true)]
    [InlineData("restricted", true)]
    public async Task Consumer_SetsWikiJsPrivacy_FromConfidentiality(string confidentiality, bool expectedPrivate)
    {
        var dbName = $"wiki-sync-{Guid.NewGuid()}";
        await using var provider = BuildHarness(dbName);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(Event("規程", "published",
                new() { ["confidentiality"] = confidentiality }), TestContext.Current.CancellationToken);
            await WaitForAsync(provider, p => p is not null);

            var wiki = provider.GetRequiredService<RecordingWikiJsClient>();
            wiki.Pushed.Should().ContainSingle();
            wiki.Pushed[0].IsPrivate.Should().Be(expectedPrivate);
        }
        finally { await harness.Stop(TestContext.Current.CancellationToken); }
    }

    // 多層防御: confidentiality 属性が欠落する場合も安全側（非公開）に倒す（deny-closed）。
    [Fact]
    public async Task Consumer_SetsWikiJsPrivate_WhenConfidentialityMissing()
    {
        var dbName = $"wiki-sync-{Guid.NewGuid()}";
        await using var provider = BuildHarness(dbName);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(Event("属性なし規程", "published", new Dictionary<string, string>()), TestContext.Current.CancellationToken);
            await WaitForAsync(provider, p => p is not null);

            var wiki = provider.GetRequiredService<RecordingWikiJsClient>();
            wiki.Pushed.Should().ContainSingle();
            wiki.Pushed[0].IsPrivate.Should().BeTrue();
        }
        finally { await harness.Stop(TestContext.Current.CancellationToken); }
    }

    // 未公開（draft 等）は同期しない。
    [Fact]
    public async Task Consumer_Ignores_NonPublishedStatus()
    {
        var dbName = $"wiki-sync-{Guid.NewGuid()}";
        await using var provider = BuildHarness(dbName);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(Event("下書き", "draft"), TestContext.Current.CancellationToken);
            (await harness.Consumed.Any<DocumentUpdated>(TestContext.Current.CancellationToken)).Should().BeTrue();

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
            db.Pages.Any(p => p.DocumentId == DocId).Should().BeFalse();
            // 未公開は Wiki.js へも反映しない。
            provider.GetRequiredService<RecordingWikiJsClient>().Pushed.Should().BeEmpty();
        }
        finally { await harness.Stop(TestContext.Current.CancellationToken); }
    }

    // 再同期で既存ページが更新される（タイトル・属性が反映される）。
    [Fact]
    public async Task Consumer_UpdatesExistingPage_OnResync()
    {
        var dbName = $"wiki-sync-{Guid.NewGuid()}";
        await using var provider = BuildHarness(dbName);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            // 1 通目を発行し、ページが作成されるまで待つ。
            await harness.Bus.Publish(Event("旧タイトル", "published",
                new() { ["confidentiality"] = "internal" }), TestContext.Current.CancellationToken);
            await WaitForAsync(provider, p => p is not null && p.Title == "旧タイトル");

            // 2 通目（同一 DocumentId）を発行し、既存ページが更新されるまで待つ。
            await harness.Bus.Publish(Event("新タイトル", "published",
                new() { ["confidentiality"] = "public" }), TestContext.Current.CancellationToken);
            await WaitForAsync(provider, p => p is not null && p.Title == "新タイトル");

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
            var pages = db.Pages.Where(p => p.DocumentId == DocId).ToList();
            pages.Should().ContainSingle();
            pages[0].Attributes["confidentiality"].Should().Be("public");
        }
        finally { await harness.Stop(TestContext.Current.CancellationToken); }
    }

    // ---- FR-19, ADR-0046 D-01: 個人資料は Wiki.js の同期対象から外す -----------------------
    //
    // 🔴 **否定形テストには陽性対照を対で置いてある。** 「常に早期 return する実装」も
    // 「doc_scope を一切見ない実装」も、否定形（private-note が同期されない）だけなら通す。
    //
    // 🔴 **Consumer_SyncsDocument_WhenDocScopeMissing の役割は「診断」である。**
    // 除外条件を `doc_scope != "organization"` と書く誤りは、変異試験の実測では 45 件中 10 件を
    // 落とす（既存 7 件のフィクスチャが doc_scope を持たないため巻き添えで落ちる）。
    // **検出そのものは既存テストでも起きる。** ただし既存テストは「機密区分の話をしているテストが
    // push 0 件で落ちる」形になり、**なぜ落ちたのかを言わない**。
    // **本テストだけが「doc_scope の欠落を個人資料と誤判定してはならない」という理由つきで落ちる。**

    // 個人資料は Wiki.js へ push されず、ABAC 同期メタデータも作られない。
    [Fact]
    public async Task Consumer_SkipsPrivateNote_NoPushAndNoMetadata()
    {
        var dbName = $"wiki-sync-{Guid.NewGuid()}";
        await using var provider = BuildHarness(dbName);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(
                Event("個人資料", "published",
                    new() { ["confidentiality"] = "restricted", ["doc_scope"] = "private-note" }),
                TestContext.Current.CancellationToken);
            (await harness.Consumed.Any<DocumentUpdated>(TestContext.Current.CancellationToken))
                .Should().BeTrue();

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
            db.Pages.Any(p => p.DocumentId == DocId).Should()
                .BeFalse("ADR-0046 D-01: Wiki.js 上に個人資料のページは作られない");
            provider.GetRequiredService<RecordingWikiJsClient>().Pushed.Should().BeEmpty();
        }
        finally { await harness.Stop(TestContext.Current.CancellationToken); }
    }

    // 陽性対照: 組織文書は従来どおり同期される（除外が広がっていないこと）。
    [Fact]
    public async Task Consumer_SyncsOrganizationDocument()
    {
        var dbName = $"wiki-sync-{Guid.NewGuid()}";
        await using var provider = BuildHarness(dbName);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(
                Event("組織文書", "published",
                    new() { ["confidentiality"] = "internal", ["doc_scope"] = "organization" }),
                TestContext.Current.CancellationToken);
            await WaitForAsync(provider, p => p is not null);

            provider.GetRequiredService<RecordingWikiJsClient>().Pushed.Should().ContainSingle();
        }
        finally { await harness.Stop(TestContext.Current.CancellationToken); }
    }

    // 🔴 陽性対照（本命）: doc_scope を持たない文書は従来どおり同期される。
    // 実データ 2,368 件はこの形であり（ADR-0054 §結果 で遡及付与しないと裁定済み）、
    // 除外を「organization でない」と書くと本テストだけが落ちる。
    [Fact]
    public async Task Consumer_SyncsDocument_WhenDocScopeMissing()
    {
        var dbName = $"wiki-sync-{Guid.NewGuid()}";
        await using var provider = BuildHarness(dbName);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            // doc_scope を持たない＝新設前から在る既存文書の形。
            await harness.Bus.Publish(
                Event("既存文書", "published", new() { ["confidentiality"] = "internal" }),
                TestContext.Current.CancellationToken);
            await WaitForAsync(provider, p => p is not null);

            provider.GetRequiredService<RecordingWikiJsClient>().Pushed.Should()
                .ContainSingle("doc_scope の欠落を個人資料と誤判定してはならない"
                             + "（実データ 2,368 件がこの形で、除外を否定で書くと全滅する）");
        }
        finally { await harness.Stop(TestContext.Current.CancellationToken); }
    }

    // 個人資料の再配信で状態が変わらない（冪等・スキップが積み上がらない）。
    [Fact]
    public async Task Consumer_SkipsPrivateNote_Idempotently_OnRedelivery()
    {
        var dbName = $"wiki-sync-{Guid.NewGuid()}";
        await using var provider = BuildHarness(dbName);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var ev = Event("個人資料", "published",
                new() { ["confidentiality"] = "restricted", ["doc_scope"] = "private-note" });
            await harness.Bus.Publish(ev, TestContext.Current.CancellationToken);
            await harness.Bus.Publish(ev, TestContext.Current.CancellationToken);
            (await harness.Consumed.Any<DocumentUpdated>(TestContext.Current.CancellationToken))
                .Should().BeTrue();

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
            db.Pages.Any(p => p.DocumentId == DocId).Should().BeFalse();
            provider.GetRequiredService<RecordingWikiJsClient>().Pushed.Should().BeEmpty();
        }
        finally { await harness.Stop(TestContext.Current.CancellationToken); }
    }

    // 境界: 個人資料であってもアーカイブ伝播は通す（除外はアーカイブ分岐より後に置いてある）。
    // ページが無くても ArchivePageAsync は冪等であり、deny-closed の向きである。
    [Fact]
    public async Task Consumer_StillPropagatesArchive_ForPrivateNote()
    {
        var dbName = $"wiki-sync-{Guid.NewGuid()}";
        await using var provider = BuildHarness(dbName);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(
                Event("個人資料", "archived",
                    new() { ["confidentiality"] = "restricted", ["doc_scope"] = "private-note" }),
                TestContext.Current.CancellationToken);
            (await harness.Consumed.Any<DocumentUpdated>(TestContext.Current.CancellationToken))
                .Should().BeTrue();

            // アーカイブ経路は push を行わない（非公開化のみ）。例外なく完走することを確かめる。
            provider.GetRequiredService<RecordingWikiJsClient>().Pushed.Should().BeEmpty();
        }
        finally { await harness.Stop(TestContext.Current.CancellationToken); }
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
        var dbName = $"wiki-sync-{Guid.NewGuid()}";
        await using var provider = BuildHarness(dbName);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            // 1 通目: 組織文書として同期される。
            await harness.Bus.Publish(
                Event("組織文書", "published",
                    new() { ["confidentiality"] = "internal", ["doc_scope"] = "organization" }),
                TestContext.Current.CancellationToken);
            await WaitForAsync(provider, p => p is not null && p.Title == "組織文書");

            // 2 通目: 同一文書が個人資料へ変わる。
            await harness.Bus.Publish(
                Event("個人資料になった", "published",
                    new() { ["confidentiality"] = "restricted", ["doc_scope"] = "private-note" }),
                TestContext.Current.CancellationToken);
            (await harness.Consumed.Any<DocumentUpdated>(TestContext.Current.CancellationToken))
                .Should().BeTrue();

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
            var page = db.Pages.Single(p => p.DocumentId == DocId);
            page.Title.Should().Be("組織文書", "以後は同期されないためタイトルは更新されない");
            // push は 1 通目の 1 回だけ。2 通目は除外される。
            provider.GetRequiredService<RecordingWikiJsClient>().Pushed.Should().ContainSingle();
        }
        finally { await harness.Stop(TestContext.Current.CancellationToken); }
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
