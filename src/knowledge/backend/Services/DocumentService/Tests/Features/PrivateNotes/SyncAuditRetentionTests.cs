using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.PrivateNotes.Maintenance;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.PrivateNotes;

// FR-20, SC-20 主要素 6, ADR-0099 決定 3, planning#618, #1446:
// 定期処理 ⑦ ——**同期履歴は 3 年で消える**（表示件数〔決定 4 の 50 件〕とは別の値である）。
//
// 時計は `RunAsync(now)` の引数で進める（既存の定期処理の試験と同じ形）。
[Trait("TestKind", "Integration")]
public class SyncAuditRetentionTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> SeedAsync(string owner, DateTimeOffset occurredAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var entry = SyncAuditEntry.Success(owner, Guid.NewGuid(), "端末", SyncDirections.Push,
            added: 1, updated: 0, deleted: 0, occurredAt);
        db.SyncAuditEntries.Add(entry);
        await db.SaveChangesAsync(Ct);
        return entry.Id;
    }

    private async Task RunMaintenanceAsync(DateTimeOffset now)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PrivateNoteMaintenanceService>()
            .RunAsync(now);
    }

    private async Task<List<Guid>> SurvivorsAsync(string owner)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        return await db.SyncAuditEntries.Where(a => a.OwnerId == owner)
            .Select(a => a.Id).ToListAsync(Ct);
    }

    // ADR-0099 決定 3: 3 年を**超えた**行だけが消える（3 年未満は残る＝陽性対照）。
    [Fact]
    public async Task 三年を超えた同期履歴は消え三年未満は残る()
    {
        var owner = $"ret-{Guid.NewGuid():N}"[..24];
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // 3 年 ＋ 1 日前（消える）／ 3 年 − 1 日前（残る）／ 直近（残る）。
        var expired = await SeedAsync(owner, now.AddYears(-3).AddDays(-1));
        var justInside = await SeedAsync(owner, now.AddYears(-3).AddDays(1));
        var recent = await SeedAsync(owner, now.AddDays(-1));

        await RunMaintenanceAsync(now);

        var survivors = await SurvivorsAsync(owner);
        survivors.Should().NotContain(expired, "保持は 3 年（ADR-0099 決定 3）");
        survivors.Should().Contain(justInside).And.Contain(recent,
            "3 年未満は残る（「全部消す」実装ではないことの陽性対照）");
    }

    // 🔴 **表示件数では消えない。** 50 件を超えた行も 3 年は残る（保持と表示を混ぜない）。
    [Fact]
    public async Task 表示件数を超えた行も保持期限までは消えない()
    {
        var owner = $"ret-{Guid.NewGuid():N}"[..24];
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 60; i++) await SeedAsync(owner, now.AddMinutes(-i));

        await RunMaintenanceAsync(now);

        (await SurvivorsAsync(owner)).Should().HaveCount(60);
    }
}
