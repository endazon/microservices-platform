using AwesomeAssertions;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using WikiService.Features.Wiki;
using WikiService.Domain;
using WikiService.Infrastructure.Persistence;
using WikiService.Domain.Ports;

namespace WikiService.Tests;

// FR-13, UC-07, IADR-0021（フォロー課題）, Issue #88: 文書の削除・アーカイブ（非公開化）の Wiki.js への伝播。
//   - 削除（DocumentDeleted）: Wiki.js ページの実体撤去（pages.delete）＋ wiki_svc メタデータ行の削除。
//     社内文書が外部システム（Wiki.js）に残り続けることを防ぐ。
//   - アーカイブ（DocumentUpdated status=archived）: Wiki.js ページの非公開化 ＋ メタデータ Archived 化。
// いずれも再配信・未同期 ID に対して冪等であること。
//
// E3a / E3b: 両段とも Wolverine になったため、本ファイルは Handle を直接呼ぶ（測るのは削除・
// アーカイブの写像であって配送ではない。登録経路は PipelineRecomposeTests が Wolverine ホストで持つ）。
public class DocumentDeleteArchiveSyncTests
{
    private static readonly Guid DocId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static ServiceProvider BuildProvider(string dbName)
        => new ServiceCollection()
            .AddLogging()
            .AddDbContext<WikiDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddSingleton<RecordingWikiJsClient>()
            .AddSingleton<IWikiJsClient>(sp => sp.GetRequiredService<RecordingWikiJsClient>())
            .AddSingleton<IWikiContentReader, StubContentReader>()
            .BuildServiceProvider(true);

    private static DocumentUpdated Updated(string title, string status)
        => new(DocId, title, status, "s3://b/doc.md",
            new Dictionary<string, string> { ["confidentiality"] = "internal" },
            ["ops"], DateTimeOffset.UtcNow);

    private static async Task HandleUpdatedAsync(ServiceProvider provider, DocumentUpdated ev)
    {
        using var scope = provider.CreateScope();
        var consumer = new DocumentSyncConsumer(
            scope.ServiceProvider.GetRequiredService<WikiDbContext>(),
            scope.ServiceProvider.GetRequiredService<IWikiJsClient>(),
            scope.ServiceProvider.GetRequiredService<IWikiContentReader>(),
            scope.ServiceProvider.GetRequiredService<ILogger<DocumentSyncConsumer>>());
        await consumer.Handle(ev, TestContext.Current.CancellationToken);
    }

    private static async Task HandleDeletedAsync(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var consumer = new DocumentDeletedConsumer(
            scope.ServiceProvider.GetRequiredService<WikiDbContext>(),
            scope.ServiceProvider.GetRequiredService<RecordingWikiJsClient>(),
            scope.ServiceProvider.GetRequiredService<ILogger<DocumentDeletedConsumer>>());
        await consumer.Handle(new DocumentDeleted(DocId, DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
    }

    private static WikiPage? PageOf(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
        return db.Pages.FirstOrDefault(p => p.DocumentId == DocId);
    }

    // アーカイブ: status=archived の受信で Wiki.js ページを非公開化し、メタデータを Archived にする。
    [Fact]
    public async Task Consumer_ArchivesPage_OnArchivedStatus()
    {
        await using var provider = BuildProvider($"wiki-arch-{Guid.NewGuid()}");
        await HandleUpdatedAsync(provider, Updated("規程", "published"));
        PageOf(provider)!.Status.Should().Be(WikiPageStatus.Active);

        await HandleUpdatedAsync(provider, Updated("規程", "archived"));
        PageOf(provider)!.Status.Should().Be(WikiPageStatus.Archived);

        // Wiki.js 側も非公開化（unpublish + private）される。
        var wiki = provider.GetRequiredService<RecordingWikiJsClient>();
        wiki.Archived.Should().Contain($"doc/{DocId}");
        // 実体撤去ではない（アーカイブは非公開化。削除は DocumentDeleted で行う）。
        wiki.Deleted.Should().BeEmpty();
    }

    // アーカイブ（未同期 ID）: メタデータが無くても例外にせず、Wiki.js 側の非公開化のみ試みる（冪等）。
    [Fact]
    public async Task Consumer_ArchiveIsIdempotent_WhenPageUnknown()
    {
        await using var provider = BuildProvider($"wiki-arch-{Guid.NewGuid()}");
        await HandleUpdatedAsync(provider, Updated("未同期", "archived"));

        var wiki = provider.GetRequiredService<RecordingWikiJsClient>();
        wiki.Archived.Should().Contain($"doc/{DocId}");
        PageOf(provider).Should().BeNull();
    }

    // アーカイブ後の再公開: published の再受信でメタデータが Active に戻る（アーカイブは可逆）。
    [Fact]
    public async Task Consumer_Reactivates_WhenRepublishedAfterArchive()
    {
        await using var provider = BuildProvider($"wiki-arch-{Guid.NewGuid()}");
        await HandleUpdatedAsync(provider, Updated("規程", "published"));
        await HandleUpdatedAsync(provider, Updated("規程", "archived"));
        PageOf(provider)!.Status.Should().Be(WikiPageStatus.Archived);

        await HandleUpdatedAsync(provider, Updated("規程（改訂）", "published"));
        var page = PageOf(provider);
        page!.Status.Should().Be(WikiPageStatus.Active);
        page.Title.Should().Be("規程（改訂）");
    }

    // 削除: DocumentDeleted の受信で Wiki.js の実体を撤去し、メタデータ行も削除する。
    [Fact]
    public async Task DeletedConsumer_RemovesWikiJsPageAndMetadata()
    {
        await using var provider = BuildProvider($"wiki-del-{Guid.NewGuid()}");
        await HandleUpdatedAsync(provider, Updated("削除予定", "published"));
        PageOf(provider).Should().NotBeNull();

        await HandleDeletedAsync(provider);
        PageOf(provider).Should().BeNull();

        // 実体撤去（社内文書の外部システム残存防止）。
        var wiki = provider.GetRequiredService<RecordingWikiJsClient>();
        wiki.Deleted.Should().Contain($"doc/{DocId}");
    }

    // 削除（未同期 ID）: メタデータが無くても例外にせず、Wiki.js 側の撤去のみ試みる（冪等・再配信安全）。
    [Fact]
    public async Task DeletedConsumer_IsIdempotent_WhenPageUnknown()
    {
        await using var provider = BuildProvider($"wiki-del-{Guid.NewGuid()}");

        // 例外が出れば本テスト自体が失敗する（未同期 ID でも正常完了すること＝冪等）。
        await HandleDeletedAsync(provider);

        var wiki = provider.GetRequiredService<RecordingWikiJsClient>();
        wiki.Deleted.Should().Contain($"doc/{DocId}");
        PageOf(provider).Should().BeNull();
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
