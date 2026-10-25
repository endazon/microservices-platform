using DocumentService.Domain.Ports;
using DocumentService.Features.Documents;
using DocumentService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    // true なら、確定の口ではなく**文書行を書く保存の直前**に窓を開ける（`DocumentRace.RaceAtSave`）。
    // 確定の関数の中で文書を読む口（タグの改名）は、確定の口の手前で開けても読み直しで追い付いてしまい、負けないためである。
    public bool AtSave { get; init; }
}

internal static class DocumentRace
{
    // 準備から操作まで、このホストで行う（台帳と記録のバスを 1 つにする）。
    internal static WebApplicationFactory<Program> Host(TestWebApplicationFactory factory, RaceTarget target)
        => factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddSingleton(target);
            if (target.AtSave)
                services.ConfigureDbContext<DocumentDbContext>((sp, o) => o.AddInterceptors(
                    new RaceAtSave(sp.GetRequiredService<IServiceScopeFactory>(), target)));
            services.RemoveAll<IDocumentUpdatedPublisher>();
            services.AddScoped<IDocumentUpdatedPublisher>(sp => new RacingPublisher(
                new RecordingDocumentUpdatedPublisher(sp.GetRequiredService<RecordingMessageBus>()),
                sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<RaceTarget>()));
            services.RemoveAll<IDocumentDeletedPublisher>();
            services.AddScoped<IDocumentDeletedPublisher>(sp => new RacingDeletedPublisher(
                new RecordingDocumentDeletedPublisher(sp.GetRequiredService<DocumentDbContext>(), sp.GetRequiredService<RecordingMessageBus>()),
                sp.GetRequiredService<DocumentDbContext>(), sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<RaceTarget>()));
        }));

    // 勝つ側の確定の中で、もう一度窓を開けない（勝つ側の保存も同じ割り込みを通る）。
    private static readonly AsyncLocal<bool> Racing = new();

    private static async Task RaceAsync(IServiceScopeFactory scopes, RaceTarget target, CancellationToken ct)
    {
        if (target.Id is not { } id || target.Remaining <= 0 || Racing.Value) return;
        Racing.Value = true;
        try
        {
            await WinAsync(scopes, target, id, ct);
        }
        finally
        {
            Racing.Value = false;
        }
    }

    private static async Task WinAsync(IServiceScopeFactory scopes, RaceTarget target, Guid id, CancellationToken ct)
    {
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
            if (!target.AtSave)
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
            if (!target.AtSave && db.ChangeTracker.Entries<DocumentService.Domain.Document>().Any(e => e.Entity.Id == target.Id))
                await RaceAsync(scopes, target, ct);
            await inner.SaveChangesAndFlushAsync(ct);
        }
    }

    // 文書行を書く保存の直前（対象の文書が更新・削除として追跡されているとき）に窓を開ける。
    private sealed class RaceAtSave(IServiceScopeFactory scopes, RaceTarget target) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } db && db.ChangeTracker.Entries<DocumentService.Domain.Document>()
                    .Any(e => e.Entity.Id == target.Id && e.State is EntityState.Modified or EntityState.Deleted))
                await RaceAsync(scopes, target, cancellationToken);
            return result;
        }
    }
}
