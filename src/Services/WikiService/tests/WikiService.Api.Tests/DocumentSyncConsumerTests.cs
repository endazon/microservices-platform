using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WikiService.Api.Consumers;
using WikiService.Api.Infrastructure;

namespace WikiService.Api.Tests;

// FR-13, UC-07: 文書更新イベント → Wiki ページ同期。
// 受け入れ基準③「文書更新後、定義時間内に反映」をイベント駆動同期で担保することの検証。
public class DocumentSyncConsumerTests
{
    private static readonly Guid DocId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ServiceProvider BuildHarness(string dbName)
        => new ServiceCollection()
            .AddDbContext<WikiDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddMassTransitTestHarness(cfg => cfg.AddConsumer<DocumentSyncConsumer>())
            .BuildServiceProvider(true);

    // 同期は非同期に完了するため、期待状態になるまで短時間ポーリングする。
    private static async Task WaitForAsync(ServiceProvider provider,
        Func<WikiService.Api.Domain.WikiPage?, bool> predicate)
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
            await harness.Bus.Publish(Event("入社手続き", "normalized"));
            (await harness.Consumed.Any<DocumentUpdated>()).Should().BeTrue();

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
            var page = db.Pages.SingleOrDefault(p => p.DocumentId == DocId);
            page.Should().NotBeNull();
            page!.Title.Should().Be("入社手続き");
            page.Attributes["confidentiality"].Should().Be("internal");
        }
        finally { await harness.Stop(); }
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
            await harness.Bus.Publish(Event("下書き", "draft"));
            (await harness.Consumed.Any<DocumentUpdated>()).Should().BeTrue();

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
            db.Pages.Any(p => p.DocumentId == DocId).Should().BeFalse();
        }
        finally { await harness.Stop(); }
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
                new() { ["confidentiality"] = "internal" }));
            await WaitForAsync(provider, p => p is not null && p.Title == "旧タイトル");

            // 2 通目（同一 DocumentId）を発行し、既存ページが更新されるまで待つ。
            await harness.Bus.Publish(Event("新タイトル", "published",
                new() { ["confidentiality"] = "public" }));
            await WaitForAsync(provider, p => p is not null && p.Title == "新タイトル");

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
            var pages = db.Pages.Where(p => p.DocumentId == DocId).ToList();
            pages.Should().ContainSingle();
            pages[0].Attributes["confidentiality"].Should().Be("public");
        }
        finally { await harness.Stop(); }
    }
}
