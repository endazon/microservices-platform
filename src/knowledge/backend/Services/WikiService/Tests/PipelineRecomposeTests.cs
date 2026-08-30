using AwesomeAssertions;
using Knowledge.Contracts.Events;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WikiService.Features.Wiki;
using WikiService.Features.Wiki.RemoveDeleted;
using WikiService.Features.Wiki.SyncDocument;
using WikiService.Domain;
using WikiService.Infrastructure.Persistence;
using WikiService.Domain.Ports;
using Wolverine;

namespace WikiService.Tests;

// FR-14, ADR-0018, IADR-0028: コード改修なしの組み替えの実証。
// 同一サービス内の 2 段（wiki-sync / wiki-delete）を宣言的構成のみで選択的に有効化し、
// 無効化した段の購読が生成されない（イベントが処理されない）ことを検証する。
//
// 🔴 ADR-0027 / E3a・E3b: 両段とも Wolverine 段になった。
// **実際にホストを起こして登録経路（AddPlatformWolverineStep）を通す**
// （E1 の PipelineStepRegistrationTests と同じ器。直接呼びだけでは登録経路の破れを測れない —— 変異 R の実測）。
public class PipelineRecomposeTests
{
    private static PipelineOptions Pipeline(bool syncEnabled, bool deleteEnabled) => new()
    {
        Steps =
        [
            new PipelineStepOptions
            {
                Name = "wiki-sync",
                Service = "wiki-service",
                Consumer = "WikiService.Features.Wiki.SyncDocument.DocumentSyncConsumer",
                Input = "DocumentUpdated",
                Outputs = [],
                Enabled = syncEnabled,
            },
            new PipelineStepOptions
            {
                Name = "wiki-delete",
                Service = "wiki-service",
                Consumer = "WikiService.Features.Wiki.RemoveDeleted.DocumentDeletedConsumer",
                Input = "DocumentDeleted",
                Outputs = [],
                Enabled = deleteEnabled,
            },
        ],
    };

    // Wolverine の器。外部トランスポートは落とし、InvokeAsync で駆動する。
    private static IHost BuildWolverineHost(PipelineOptions pipeline, string dbName)
        => new HostBuilder()
            .UseWolverine(opts =>
            {
                opts.AddPlatformWolverineStep<DocumentSyncConsumer>(pipeline);
                opts.AddPlatformWolverineStep<DocumentDeletedConsumer>(pipeline);

                // 🔴 規約探索を意図的に「効いている」状態にする（器を甘くしない。E1 実測:
                // 走査対象の決まり方は環境依存で、明示しないと enabled:false の試験が手元だけ緑になる）。
                opts.Discovery.IncludeAssembly(typeof(DocumentDeletedConsumer).Assembly);

                // 本番（Program.cs）と同じ既定を必ず通す（器の乖離は本番に無い失敗を作る）。
                opts.UsePlatformMessagingDefaults();
            })
            .ConfigureServices(services => services
                .AddLogging()
                .AddDbContext<WikiDbContext>(o => o.UseInMemoryDatabase(dbName))
                .AddSingleton<IWikiJsClient, NoopWikiJsClient>()
                .AddSingleton<IWikiContentReader, NoopContentReader>()
                .DisableAllExternalWolverineTransports())
            .Build();

    private static DocumentUpdated Updated(Guid docId) => new(
        docId, "組み替えテスト", "published", "s3://b/doc.md",
        new Dictionary<string, string> { ["confidentiality"] = "internal" },
        ["ops"], DateTimeOffset.UtcNow);

    [Fact]
    public async Task 構成のみで同期段を外し削除段だけを有効化できる()
    {
        var dbName = Guid.NewGuid().ToString();
        using var host = BuildWolverineHost(Pipeline(syncEnabled: false, deleteEnabled: true), dbName);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var bus = host.Services.GetRequiredService<IMessageBus>();

        // wiki-delete は処理される（削除段は有効のまま）。
        var docId = Guid.NewGuid();
        await SeedPageAsync(host, docId);
        await bus.InvokeAsync(new DocumentDeleted(docId, DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
        PageExists(host, docId).Should().BeFalse("削除段が処理したメタデータ行は消える");

        // DocumentUpdated は購読されない（同期段は構成で無効。ハンドラが無いのでプロセス内呼び出しは失敗する）。
        var act = () => bus.InvokeAsync(Updated(Guid.NewGuid()), TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task 有効な同期段は登録経路を通って処理される()
    {
        var dbName = Guid.NewGuid().ToString();
        using var host = BuildWolverineHost(Pipeline(syncEnabled: true, deleteEnabled: true), dbName);
        await host.StartAsync(TestContext.Current.CancellationToken);

        var docId = Guid.NewGuid();
        await host.Services.GetRequiredService<IMessageBus>()
            .InvokeAsync(Updated(docId), TestContext.Current.CancellationToken);

        PageExists(host, docId).Should().BeTrue("同期段が処理したメタデータ行が作られる");
    }

    [Fact]
    public async Task 無効化した削除段は登録されず購読されない()
    {
        // 規則8: enabled: false → 登録しない（構成のみで段を外せる＝FR-14）。
        // 規約探索を効かせた状態で、それでも購読が生えないことを見る。
        using var host = BuildWolverineHost(
            Pipeline(syncEnabled: false, deleteEnabled: false), Guid.NewGuid().ToString());
        await host.StartAsync(TestContext.Current.CancellationToken);

        // ハンドラが登録されていないので、プロセス内呼び出しは「宛先なし」で失敗する。
        var act = () => host.Services.GetRequiredService<IMessageBus>()
            .InvokeAsync(new DocumentDeleted(Guid.NewGuid(), DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<Exception>();
    }

    private static async Task SeedPageAsync(IHost host, Guid docId)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
        db.Pages.Add(WikiPage.CreateFromDocument(
            docId, "recompose", "s3://b/doc.md",
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, ["ops"]));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static bool PageExists(IHost host, Guid docId)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
        return db.Pages.Any(p => p.DocumentId == docId);
    }
}

file sealed class NoopWikiJsClient : IWikiJsClient
{
    public Task UpsertPageAsync(WikiJsPage page, CancellationToken ct = default) => Task.CompletedTask;
    public Task ArchivePageAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeletePageAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string?> GetRenderedContentAsync(string path, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}

file sealed class NoopContentReader : IWikiContentReader
{
    public Task<string> ReadAsync(string? markdownUri, string title, CancellationToken ct = default)
        => Task.FromResult($"# {title}");
}
