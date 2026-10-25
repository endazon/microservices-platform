using DocumentService.Domain.Ports;
using DocumentService.Features.Documents;
using DocumentService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocumentService.Tests.Features.Documents;

// FR-02, IADR-0637 (#2150 段 1a・1b・1c): 並行する確定の窓を決定的に開ける試験の器。
//
// 確定の口（`IDocumentUpdatedPublisher.CommitAsync` と `IDocumentDeletedPublisher.SaveChangesAndFlushAsync`）が本来の確定を走らせる
// 直前に、別のスコープで対象の文書の発行を 1 回確定させる（勝つ側）。本来の確定はその後に走り、読んだ連番が古いので負ける。
// `Remaining` の回数だけ窓を開ける（読み直してやり直す口が、上限まで負け続ける場合を作る）。
// 削除の発行口は、確定する文脈が対象の文書を追跡しているときだけ窓を開ける（定期処理は所有者ごとに確定するので、
// 別の所有者の確定で窓を使い切らないようにする）。
internal sealed class RaceTarget
{
    public Guid? Id { get; set; }
    public int Remaining { get; set; } = 1;
    public int Raced { get; set; }
}

internal static class DocumentRace
{
    // 準備から操作まで、このホストで行う（台帳と記録のバスを 1 つにする）。
    internal static WebApplicationFactory<Program> Host(TestWebApplicationFactory factory, RaceTarget target)
        => factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddSingleton(target);
            services.RemoveAll<IDocumentUpdatedPublisher>();
            services.AddScoped<IDocumentUpdatedPublisher>(sp => new RacingPublisher(
                new RecordingDocumentUpdatedPublisher(sp.GetRequiredService<RecordingMessageBus>()),
                sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<RaceTarget>()));
            services.RemoveAll<IDocumentDeletedPublisher>();
            services.AddScoped<IDocumentDeletedPublisher>(sp => new RacingDeletedPublisher(
                new RecordingDocumentDeletedPublisher(sp.GetRequiredService<DocumentDbContext>(), sp.GetRequiredService<RecordingMessageBus>()),
                sp.GetRequiredService<DocumentDbContext>(), sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<RaceTarget>()));
        }));

    private static async Task RaceAsync(IServiceScopeFactory scopes, RaceTarget target, CancellationToken ct)
    {
        if (target.Id is not { } id || target.Remaining <= 0) return;
        target.Remaining--;
        target.Raced++;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var winnerBus = new RecordingDocumentUpdatedPublisher(scope.ServiceProvider.GetRequiredService<RecordingMessageBus>());
        var doc = await db.Documents.FindAsync([id], ct);
        await DocumentEndpoints.SaveChangesAndPublishUpdatedIfIndexableAsync(winnerBus, db, doc!, ct);
    }

    private sealed class RacingPublisher(RecordingDocumentUpdatedPublisher inner, IServiceScopeFactory scopes, RaceTarget target)
        : IDocumentUpdatedPublisher
    {
        public Task PublishUpdatedAsync(Guid documentId, string title, string status, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags, DateTimeOffset updatedAt,
            string? contentFingerprint = null, bool hasBody = true, string? originalPath = null, string? dataSourceName = null,
            List<string>? sharedWith = null, long? sequence = null, CancellationToken ct = default)
            => inner.PublishUpdatedAsync(documentId, title, status, markdownUri, attributes, tags, updatedAt,
                contentFingerprint, hasBody, originalPath, dataSourceName, sharedWith, sequence, ct);

        public async Task CommitAsync(Func<CancellationToken, Task> saveAndPublish, CancellationToken ct = default)
        {
            await RaceAsync(scopes, target, ct);
            await inner.CommitAsync(saveAndPublish, ct);
        }
    }

    // 完全削除は削除の発行口で確定する。確定の直前に同じ窓を開ける。
    private sealed class RacingDeletedPublisher(RecordingDocumentDeletedPublisher inner, DocumentDbContext db,
        IServiceScopeFactory scopes, RaceTarget target)
        : IDocumentDeletedPublisher
    {
        public Task PublishDeletedAsync(Guid documentId, DateTimeOffset deletedAt, long? sequence = null, CancellationToken ct = default)
            => inner.PublishDeletedAsync(documentId, deletedAt, sequence, ct);

        public async Task SaveChangesAndFlushAsync(CancellationToken ct = default)
        {
            if (db.ChangeTracker.Entries<DocumentService.Domain.Document>().Any(e => e.Entity.Id == target.Id))
                await RaceAsync(scopes, target, ct);
            await inner.SaveChangesAndFlushAsync(ct);
        }
    }
}
