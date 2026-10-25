using AwesomeAssertions;
using ConversionService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ConversionService.Tests.Infrastructure.Persistence;

// 🔴 FR-12, UC-06, #2159: **同じジョブへの再変換が並行しても、再発行は 1 回だけ。**
//
// `PrepareRetryAsync` は読む → 判定（failed のときだけ queued へ）→ 保存の check-then-write である。窓は「読んだ後・保存の前」にある。
// 本試験はその窓を**決定的に**開ける —— 先の再変換（A）の保存の直前に割り込み、後の再変換（B）を最後まで走らせてから A の保存を続ける。
// 状態は並行トークンなので A の `UPDATE … WHERE "Status" = 'failed'` は 0 行になり、A は null（発行しない）を返す。
// 🔴 並行トークン（`ConversionJobDbContext` の `Status.IsConcurrencyToken()`）を外すと、A も B もイベントを返しここで赤になる。
// 実 PostgreSQL での同じ窓は `Knowledge.IntegrationTests` の `ConversionRetryOutboxTests` が測る（行ロック待ちを確かめてから先を確定する）。
[Trait("TestKind", "Unit")]
public class ConversionRetryConcurrencyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RawDocumentFetched Raw(Guid id) =>
        new(id, Guid.NewGuid(), "filesystem", "/docs/a.docx", $"storage://{id}/raw",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            new Dictionary<string, string>(), [], DateTimeOffset.UtcNow);

    private static ConversionJobDbContext NewDb(string name, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<ConversionJobDbContext>()
            .UseInMemoryDatabase(name)
            .AddInterceptors(interceptors)
            .Options);

    // 保存の直前に 1 度だけ割り込みを走らせる。
    private sealed class BeforeFirstSave(Func<Task> interrupt) : SaveChangesInterceptor
    {
        private bool _done;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_done)
            {
                _done = true;
                await interrupt();
            }
            return result;
        }
    }

    private static async Task<(string Db, Guid Id)> FailedJobAsync()
    {
        var name = Guid.NewGuid().ToString();
        var id = Guid.NewGuid();
        await using var db = NewDb(name);
        var store = new EfConversionJobStore(db);
        await store.StartAsync(Raw(id), Ct);
        await store.FailAsync(id, "失敗", ct: Ct);
        return (name, id);
    }

    [Fact]
    public async Task 読んだ後に別の再変換が先に確定したら負けた側は発行しない()
    {
        var (name, id) = await FailedJobAsync();
        RawDocumentFetched? winner = null;
        await using var dbA = NewDb(name, new BeforeFirstSave(async () =>
        {
            await using var dbB = NewDb(name);
            winner = await new EfConversionJobStore(dbB).PrepareRetryAsync(id, ct: Ct);
        }));

        var loser = await new EfConversionJobStore(dbA).PrepareRetryAsync(id, ct: Ct);

        winner.Should().NotBeNull("陽性対照: 先に確定した再変換は発行する");
        loser.Should().BeNull("読んだ failed が保存の時点で queued に変わっていた —— 2 回目の発行をさせない");
        await using var reader = NewDb(name);
        (await new EfConversionJobStore(reader).GetAsync(id, Ct))!.Status.Should().Be(ConversionJobStatus.Queued);
        dbA.ChangeTracker.Entries().Should().BeEmpty("負けたジョブと図の追跡は外す（同じ確定の後続の保存が書き戻さない）");
    }

    // 境界: 割り込みがジョブの状態を変えなければ（並行していない）、従前どおり再変換できる。
    [Fact]
    public async Task 状態が変わらなければ従前どおり再変換できる()
    {
        var (name, id) = await FailedJobAsync();
        await using var dbA = NewDb(name, new BeforeFirstSave(async () =>
        {
            await using var dbB = NewDb(name);
            (await new EfConversionJobStore(dbB).GetAsync(id, Ct))!.Status.Should().Be(ConversionJobStatus.Failed);
        }));

        (await new EfConversionJobStore(dbA).PrepareRetryAsync(id, ct: Ct)).Should().NotBeNull();
    }

    // 状態の並行トークンは列を足さない。モデルとスナップショットに差が無い（マイグレーション不要）。
    [Fact]
    public void スナップショットとモデルに差が無い()
    {
        using var db = new ConversionJobDbContext(new DbContextOptionsBuilder<ConversionJobDbContext>()
            .UseNpgsql("Host=unused.invalid;Database=unused").Options);

        db.Database.HasPendingModelChanges().Should().BeFalse();
        db.Model.FindEntityType(typeof(ConversionService.Domain.ConversionJob))!
            .FindProperty(nameof(ConversionService.Domain.ConversionJob.Status))!.IsConcurrencyToken.Should().BeTrue();
    }
}
