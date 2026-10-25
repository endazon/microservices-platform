using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Wolverine;
using global::GraphService.Features.GraphDocuments.Delete;
using global::GraphService.Features.GraphDocuments.Sync;
using global::GraphService.Infrastructure.Persistence;

namespace Knowledge.IntegrationTests.GraphService;

// 🔴 FR-17, FR-19, IADR-0639 決定 1・2 (#2150 / #2251) 段 2: **グラフの墓石を実 PostgreSQL で測る。**
//
// 単体の試験（InMemory）は時刻をティックのまま保存するので、保存の精度（timestamptz はマイクロ秒）への丸めを外す変異を捕まえられない。
// ここでは本番と同じ受け口（DI から作る）・同じ文書の排他（`pg_advisory_xact_lock`）・同じ保存（migration 済みの表）で、
// 規則 11 の表の P3（撤収の後の古い ON）と P5（削除の後の古い更新）を確かめる。時刻はマイクロ秒より細かい端数つきである。
// 外部のトランスポートは落とし、ブローカに依らない（受け口を直接呼ぶ）。
[Trait("Category", "Integration")]
public sealed class GraphSyncMarkPostgresTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // マイクロ秒より細かい端数つきの時刻（丸めを外すと、同じ時刻の古い版を端数の分だけ新しいと読む）。
    private static readonly DateTimeOffset T = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero).AddTicks(3);

    private GraphServiceFactory _root = null!;
    private WebApplicationFactory<global::GraphService.GraphServiceTestMarker> _factory = null!;

    public async ValueTask InitializeAsync()
    {
        if (!postgres.IsAvailable) return;
        _root = new GraphServiceFactory(postgres, rabbit);
        _factory = _root.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.DisableAllExternalWolverineTransports();
            // 定期処理（クラスタ検出・要約・健全性）と MassTransit の Bus を起こさない（受け口だけを測る）。
            foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService)
                         && (d.ImplementationType?.Namespace?.StartsWith("GraphService", StringComparison.Ordinal) == true
                             || d.ImplementationType?.Namespace?.StartsWith("MassTransit", StringComparison.Ordinal) == true)).ToList())
                services.Remove(hosted);
        }));
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<GraphDbContext>().Database.MigrateAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        if (_root is not null) await _root.DisposeAsync();
    }

    private async Task UpdateAsync(DocumentUpdated ev)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await ActivatorUtilities.CreateInstance<GraphDocumentSyncConsumer>(scope.ServiceProvider).Handle(ev, Ct);
    }

    private async Task DeleteAsync(DocumentDeleted ev)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await ActivatorUtilities.CreateInstance<DocumentDeletedConsumer>(scope.ServiceProvider).Handle(ev, Ct);
    }

    private async Task<bool> NodeExistsAsync(Guid id)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<GraphDbContext>().Documents.AnyAsync(d => d.DocumentId == id, Ct);
    }

    private static DocumentUpdated On(Guid id, DateTimeOffset at, long sequence)
        => new(id, "墓石", "published", "storage://b/doc.md",
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, [], at, "fp-1", Sequence: sequence);

    private static DocumentUpdated Off(Guid id, DateTimeOffset at, long sequence)
        => On(id, at, sequence) with { Attributes = DocumentExposure.WithAllExcluded(new Dictionary<string, string> { ["confidentiality"] = "internal" }) };

    [Fact]
    public async Task P3_撤収の後に遅れて届いた古いONは実PostgreSQLでもノードを作り直さない()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);
        var id = Guid.NewGuid();
        await UpdateAsync(On(id, T, 5));
        await UpdateAsync(Off(id, T, 6));
        (await NodeExistsAsync(id)).Should().BeFalse("前提: 撤収した");

        await UpdateAsync(On(id, T, 5));

        (await NodeExistsAsync(id)).Should().BeFalse("墓石が残っており、同じ時刻（マイクロ秒へ丸めた）で連番の小さい ON は古い（#2251）");
    }

    [Fact]
    public async Task P5_削除の後に遅れて届いた古い更新は実PostgreSQLでもノードを作り直さず_作り直しは反映する()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);
        var id = Guid.NewGuid();
        await UpdateAsync(On(id, T, 8));
        await DeleteAsync(new DocumentDeleted(id, T, Sequence: 9));

        await UpdateAsync(On(id, T, 8));
        (await NodeExistsAsync(id)).Should().BeFalse("削除と同じ時刻の古い更新は捨てる");

        await UpdateAsync(On(id, T.AddSeconds(1), 1));
        (await NodeExistsAsync(id)).Should().BeTrue("P2: 同じ ID で作り直した文書（連番は 1 から）は反映する");
    }
}
