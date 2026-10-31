using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Features.SyncSettings.Pause;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.SyncSettings;

// FR-20, SC-20 主要素 7, 計画 ADR-0145 決定 1・3, [[IADR-0667]] (#2378 の AI レビュー 🟡2・🟡3):
// **同時の一時停止を 1 つの停止期間に収める。**
//
// 🔴 競合は時刻任せにしない。保存の直前に割り込む `SaveChangesInterceptor` で、もう 1 本の要求を**必ず**先に確定させる
// （HTTP を 2 本同時に投げるだけでは、交互に走る保証が無く、緑が「たまたま」になる）。
[Trait("TestKind", "Integration")]
public class SyncPauseConcurrencyTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewUser(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    // 最初の保存の直前に 1 回だけ `race` を走らせる（もう 1 本の要求が先に確定した状態を作る）。
    // 🔴 `duplicateKey`: InMemory は主キーの重複を `ArgumentException` で返し、PostgreSQL（本番）の一意制約違反
    //   （`DbUpdateException`）と形が違う。初回の作成の衝突は、本番の形（`DbUpdateException`）をここで投げて再現する。
    private sealed class RaceOnFirstSave(Func<Task> race, bool duplicateKey = false) : SaveChangesInterceptor
    {
        private int _fired;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await race();
                if (duplicateKey)
                    throw new DbUpdateException("duplicate key value violates unique constraint \"PK_SyncSettings\"");
            }
            return result;
        }
    }

    private DocumentDbContext NewContext(IServiceScope scope, SaveChangesInterceptor? interceptor = null)
    {
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<DocumentDbContext>>();
        var builder = new DbContextOptionsBuilder<DocumentDbContext>(options);
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        return new DocumentDbContext(builder.Options);
    }

    private async Task<(Guid? Winner, DocumentService.Domain.SyncSettings Loser, bool LoserChanged)> RacePausesAsync(string owner,
        bool duplicateKey = false)
    {
        using var scope = factory.Services.CreateScope();
        Guid? winner = null;
        var interceptor = new RaceOnFirstSave(async () =>
        {
            await using var other = NewContext(scope);
            var (won, changed) = await PauseSyncEndpoint.ApplyStateAsync(other, owner, pause: true,
                DateTimeOffset.UtcNow, Ct);
            changed.Should().BeTrue("割り込んだ側が先に停止する（前提）");
            winner = won.PausePeriodId;
        }, duplicateKey);
        await using var db = NewContext(scope, interceptor);
        var (loser, loserChanged) = await PauseSyncEndpoint.ApplyStateAsync(db, owner, pause: true,
            DateTimeOffset.UtcNow, Ct);
        return (winner, loser, loserChanged);
    }

    private async Task<DocumentService.Domain.SyncSettings> StoredAsync(string owner)
    {
        using var scope = factory.Services.CreateScope();
        await using var db = NewContext(scope);
        return await db.SyncSettings.AsNoTracking().SingleAsync(s => s.OwnerId == owner, Ct);
    }

    // 🟡2: 行が無い利用者への初回の一時停止が同時に 2 本 → 主キーの衝突を読み直し、冪等に終える（500 にしない）。
    [Fact]
    public async Task 初回の一時停止が同時に2本来ても1つの停止期間に収まり後の1本は冪等に終わる()
    {
        var owner = NewUser("prace1");
        var (winner, loser, changed) = await RacePausesAsync(owner, duplicateKey: true);

        winner.Should().NotBeNull();
        changed.Should().BeFalse("後の 1 本は、読み直した行が既に停止中なので何もしない（冪等）");
        loser.PausePeriodId.Should().Be(winner, "後勝ちで別の停止期間を書かない");
        (await StoredAsync(owner)).PausePeriodId.Should().Be(winner);
    }

    // 🟡3: 未停止の既存の行への一時停止が同時に 2 本 → 同時実行トークンの衝突を読み直し、停止期間を割らない。
    [Fact]
    public async Task 未停止の行への一時停止が同時に2本来ても停止期間は割れない()
    {
        var owner = NewUser("prace2");
        using (var scope = factory.Services.CreateScope())
        {
            await using var db = NewContext(scope);
            await PauseSyncEndpoint.ApplyStateAsync(db, owner, pause: true, DateTimeOffset.UtcNow, Ct);
            await PauseSyncEndpoint.ApplyStateAsync(db, owner, pause: false, DateTimeOffset.UtcNow, Ct);
        }
        (await StoredAsync(owner)).IsPaused.Should().BeFalse("前提: 行は在り、未停止");

        var (winner, loser, changed) = await RacePausesAsync(owner);

        changed.Should().BeFalse();
        loser.PausePeriodId.Should().Be(winner);
        (await StoredAsync(owner)).PausePeriodId.Should().Be(winner);
    }

    // 同時実行トークンは行の全更新に効く。フォルダの更新が同時の一時停止と衝突しても、500 にせず、停止の状態を消さずに当たる。
    [Fact]
    public async Task フォルダの更新が同時の一時停止と衝突しても停止の状態を残して当たる()
    {
        var owner = NewUser("prace3");
        var session = factory.CreateClient();
        session.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, owner);
        (await session.PutAsJsonAsync("/private-notes/sync-settings/",
            new { targetFolders = new[] { "a" } }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        Guid? period = null;
        var interceptor = new RaceOnFirstSave(async () =>
        {
            await using var other = NewContext(scope);
            period = (await PauseSyncEndpoint.ApplyStateAsync(other, owner, pause: true, DateTimeOffset.UtcNow, Ct))
                .Settings.PausePeriodId;
        });
        await using (var db = NewContext(scope, interceptor))
        {
            var settings = await db.SyncSettings.SingleAsync(s => s.OwnerId == owner, Ct);
            settings.Replace(["b"], DateTimeOffset.UtcNow);
            var save = () => db.SaveChangesAsync(Ct);
            await save.Should().ThrowAsync<DbUpdateConcurrencyException>("前提: 同時実行トークンが衝突を検出する");
        }

        // 端点は衝突を読み直して当てる（同じ状況を HTTP で: 先に停止した行へフォルダを当てても停止は残る）。
        var put = await session.PutAsJsonAsync("/private-notes/sync-settings/", new { targetFolders = new[] { "b" } }, Ct);
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await put.Content.ReadFromJsonAsync<SyncSettingsDto>(Ct);
        dto!.TargetFolders.Select(f => f.Path).Should().Equal("b");
        dto.SyncPaused.Should().BeTrue("フォルダの更新は停止の状態を消さない");
        (await StoredAsync(owner)).PausePeriodId.Should().Be(period);
    }
}
