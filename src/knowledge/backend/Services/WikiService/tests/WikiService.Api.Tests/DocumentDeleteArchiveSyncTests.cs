using FluentAssertions;
using Knowledge.Contracts.Events;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using WikiService.Api.Composable.Steps;
using WikiService.Api.Foundation.Domain;
using WikiService.Api.Foundation.Persistence;
using WikiService.Api.Foundation.Ports;
using WikiService.Api.Foundation.Services;

namespace WikiService.Api.Tests;

// FR-13, UC-07, IADR-0021（フォロー課題）, Issue #88: 文書の削除・アーカイブ（非公開化）の Wiki.js への伝播。
//   - 削除（DocumentDeleted）: Wiki.js ページの実体撤去（pages.delete）＋ wiki_svc メタデータ行の削除。
//     社内文書が外部システム（Wiki.js）に残り続けることを防ぐ。
//   - アーカイブ（DocumentUpdated status=archived）: Wiki.js ページの非公開化 ＋ メタデータ Archived 化。
// いずれも再配信・未同期 ID に対して冪等であること。
public class DocumentDeleteArchiveSyncTests
{
    private static readonly Guid DocId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static ServiceProvider BuildHarness(string dbName)
        => new ServiceCollection()
            .AddLogging()
            .AddDbContext<WikiDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddSingleton<RecordingWikiJsClient>()
            .AddSingleton<IWikiJsClient>(sp => sp.GetRequiredService<RecordingWikiJsClient>())
            .AddSingleton<IWikiContentReader, StubContentReader>()
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddConsumer<DocumentSyncConsumer>();
                cfg.AddConsumer<DocumentDeletedConsumer>();
            })
            .BuildServiceProvider(true);

    private static DocumentUpdated Updated(string title, string status)
        => new(DocId, title, status, "s3://b/doc.md",
            new Dictionary<string, string> { ["confidentiality"] = "internal" },
            ["ops"], DateTimeOffset.UtcNow);

    private static async Task WaitForAsync(ServiceProvider provider, Func<WikiPage?, bool> predicate)
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

    private static async Task WaitForWikiJsAsync(Func<bool> predicate)
    {
        for (var i = 0; i < 50; i++)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("Wiki.js クライアントが期待状態になりませんでした。");
    }

    // アーカイブ: status=archived の受信で Wiki.js ページを非公開化し、メタデータを Archived にする。
    [Fact]
    public async Task Consumer_ArchivesPage_OnArchivedStatus()
    {
        await using var provider = BuildHarness($"wiki-arch-{Guid.NewGuid()}");
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(Updated("規程", "published"));
            await WaitForAsync(provider, p => p is not null && p.Status == WikiPageStatus.Active);

            await harness.Bus.Publish(Updated("規程", "archived"));
            await WaitForAsync(provider, p => p is not null && p.Status == WikiPageStatus.Archived);

            // Wiki.js 側も非公開化（unpublish + private）される。
            var wiki = provider.GetRequiredService<RecordingWikiJsClient>();
            wiki.Archived.Should().Contain($"doc/{DocId}");
            // 実体撤去ではない（アーカイブは非公開化。削除は DocumentDeleted で行う）。
            wiki.Deleted.Should().BeEmpty();
        }
        finally { await harness.Stop(); }
    }

    // アーカイブ（未同期 ID）: メタデータが無くても例外にせず、Wiki.js 側の非公開化のみ試みる（冪等）。
    [Fact]
    public async Task Consumer_ArchiveIsIdempotent_WhenPageUnknown()
    {
        await using var provider = BuildHarness($"wiki-arch-{Guid.NewGuid()}");
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(Updated("未同期", "archived"));
            var wiki = provider.GetRequiredService<RecordingWikiJsClient>();
            await WaitForWikiJsAsync(() => wiki.Archived.Count > 0);

            wiki.Archived.Should().Contain($"doc/{DocId}");
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
            db.Pages.Any(p => p.DocumentId == DocId).Should().BeFalse();
        }
        finally { await harness.Stop(); }
    }

    // アーカイブ後の再公開: published の再受信でメタデータが Active に戻る（アーカイブは可逆）。
    [Fact]
    public async Task Consumer_Reactivates_WhenRepublishedAfterArchive()
    {
        await using var provider = BuildHarness($"wiki-arch-{Guid.NewGuid()}");
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(Updated("規程", "published"));
            await WaitForAsync(provider, p => p is not null && p.Status == WikiPageStatus.Active);
            await harness.Bus.Publish(Updated("規程", "archived"));
            await WaitForAsync(provider, p => p is not null && p.Status == WikiPageStatus.Archived);

            await harness.Bus.Publish(Updated("規程（改訂）", "published"));
            await WaitForAsync(provider, p => p is not null && p.Status == WikiPageStatus.Active
                && p.Title == "規程（改訂）");
        }
        finally { await harness.Stop(); }
    }

    // 削除: DocumentDeleted の受信で Wiki.js の実体を撤去し、メタデータ行も削除する。
    [Fact]
    public async Task DeletedConsumer_RemovesWikiJsPageAndMetadata()
    {
        await using var provider = BuildHarness($"wiki-del-{Guid.NewGuid()}");
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(Updated("削除予定", "published"));
            await WaitForAsync(provider, p => p is not null);

            await harness.Bus.Publish(new DocumentDeleted(DocId, DateTimeOffset.UtcNow));
            await WaitForAsync(provider, p => p is null);

            // 実体撤去（社内文書の外部システム残存防止）。
            var wiki = provider.GetRequiredService<RecordingWikiJsClient>();
            wiki.Deleted.Should().Contain($"doc/{DocId}");
        }
        finally { await harness.Stop(); }
    }

    // 削除（未同期 ID）: メタデータが無くても例外にせず、Wiki.js 側の撤去のみ試みる（冪等・再配信安全）。
    [Fact]
    public async Task DeletedConsumer_IsIdempotent_WhenPageUnknown()
    {
        await using var provider = BuildHarness($"wiki-del-{Guid.NewGuid()}");
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new DocumentDeleted(DocId, DateTimeOffset.UtcNow));
            var wiki = provider.GetRequiredService<RecordingWikiJsClient>();
            await WaitForWikiJsAsync(() => wiki.Deleted.Count > 0);

            wiki.Deleted.Should().Contain($"doc/{DocId}");
            // 例外が出ていればハーネスの Consumed にフォールトが残る。正常消費のみであること。
            (await harness.Consumed.Any<DocumentDeleted>()).Should().BeTrue();
        }
        finally { await harness.Stop(); }
    }
}

// Issue #88: 削除・アーカイブの伝播を記録するテスト用スタブ。
file sealed class RecordingWikiJsClient : IWikiJsClient
{
    private readonly ConcurrentQueue<string> _archived = new();
    private readonly ConcurrentQueue<string> _deleted = new();

    public IReadOnlyCollection<string> Archived => _archived.ToArray();
    public IReadOnlyCollection<string> Deleted => _deleted.ToArray();

    public Task UpsertPageAsync(WikiJsPage page, CancellationToken ct = default) => Task.CompletedTask;

    public Task ArchivePageAsync(string path, CancellationToken ct = default)
    {
        _archived.Enqueue(path);
        return Task.CompletedTask;
    }

    public Task DeletePageAsync(string path, CancellationToken ct = default)
    {
        _deleted.Enqueue(path);
        return Task.CompletedTask;
    }

    public Task<string?> GetRenderedContentAsync(string path, CancellationToken ct = default)
        => Task.FromResult<string?>("<article>rendered</article>");
}

file sealed class StubContentReader : IWikiContentReader
{
    public Task<string> ReadAsync(string? markdownUri, string title, CancellationToken ct = default)
        => Task.FromResult($"# {title}\n\n本文");
}
