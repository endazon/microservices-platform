using AwesomeAssertions;
using ConversionService.Domain.Ports;
using ConversionService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ConversionService.Tests.Infrastructure.Persistence;

// 🔴 FR-12, UC-06, #2255: **補正の記録は、書く時点のジョブが変換中なら書かず、保存が状態の変化に負けたら投げずに JobChanged を返す。**
//
// 窓 A（記録の読み → 保存の間）: 保存の直前に割り込み、別のスコープで再処理を始める（processing）。状態は並行トークンなので保存は 0 行になる。
// 捕まえなければ DbUpdateConcurrencyException が補正の口まで出て 500 になる。
// 窓 B（補正の口の確かめ → 記録の読みの間）: 先に processing にしてから記録する。確かめ直さなければ、照合は通って黙って書く。
// 端点の 409 job_changed は `ConversionFigureCorrectionTests` の P1・P2 が測る。
[Trait("TestKind", "Unit")]
public class ConversionCorrectionConcurrencyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RawDocumentFetched Raw(Guid id) =>
        new(id, Guid.NewGuid(), "filesystem", "/docs/a.docx", $"storage://{id}/raw",
            "application/pdf", new Dictionary<string, string>(), [], DateTimeOffset.UtcNow);

    private static ConversionJobDbContext NewDb(string name, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<ConversionJobDbContext>()
            .UseInMemoryDatabase(name)
            .AddInterceptors(interceptors)
            .Options);

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

    // 縮退した図 1 件を持つ成功ジョブ。
    private static async Task<(string Db, Guid Id)> SucceededAsync()
    {
        var name = Guid.NewGuid().ToString();
        var id = Guid.NewGuid();
        await using var db = NewDb(name);
        var store = new EfConversionJobStore(db);
        await store.StartAsync(Raw(id), Ct);
        await store.SucceedAsync(id, Guid.NewGuid(), "storage://normalized/doc.md",
            [new NormalizedFigure("fig-1", false, null, null, "storage://normalized/assets/fig-1.png", "image/png", null)],
            ct: Ct);
        return (name, id);
    }

    private static async Task StartReprocessingAsync(string name, Guid id)
    {
        await using var other = NewDb(name);
        await new EfConversionJobStore(other).StartAsync(Raw(id), Ct);
    }

    private static async Task<(ConversionJobDto Job, IReadOnlyList<ConversionFigureDto> Figures)> ReadAsync(string name, Guid id)
    {
        await using var reader = NewDb(name);
        var store = new EfConversionJobStore(reader);
        return ((await store.GetAsync(id, Ct))!, (await store.ListFiguresAsync(id, Ct))!);
    }

    [Fact]
    public async Task P1_記録を読んだ後_保存の前に再処理が始まったら投げずにJobChangedを返し記録しない()
    {
        var (name, id) = await SucceededAsync();
        await using var dbA = NewDb(name, new BeforeFirstSave(() => StartReprocessingAsync(name, id)));

        var written = await new EfConversionJobStore(dbA)
            .ApplyCorrectionAsync(id, "fig-1", "mermaid", "flowchart LR; X-->Y;", "storage://normalized/doc.md", Ct);

        written.Should().Be(CorrectionWrite.JobChanged, "並行トークンの照合に負けた保存を 500 の元（例外）にしない");
        var (job, figures) = await ReadAsync(name, id);
        job.Status.Should().Be(ConversionJobStatus.Processing, "再処理の遷移は残る");
        figures.Should().NotContain(f => f.Corrected, "補正は記録しない");
        dbA.ChangeTracker.Entries().Should().BeEmpty("負けたジョブと図の追跡は外す（同じ確定の後続の保存が書き戻さない）");
    }

    [Fact]
    public async Task P2_記録を読む前に再処理が始まっていたら書かずにJobChangedを返す()
    {
        var (name, id) = await SucceededAsync();
        await StartReprocessingAsync(name, id);
        await using var db = NewDb(name);

        var written = await new EfConversionJobStore(db)
            .ApplyCorrectionAsync(id, "fig-1", "mermaid", "flowchart LR; X-->Y;", "storage://normalized/doc.md", Ct);

        written.Should().Be(CorrectionWrite.JobChanged, "読んだ processing のままなら照合は通る。確かめ直さないと黙って書く");
        var (_, figures) = await ReadAsync(name, id);
        figures.Should().NotContain(f => f.Corrected);
    }

    [Fact]
    public async Task P3_並行しなければ補正を記録する()
    {
        var (name, id) = await SucceededAsync();
        await using var db = NewDb(name);

        var written = await new EfConversionJobStore(db)
            .ApplyCorrectionAsync(id, "fig-1", "mermaid", "flowchart LR; X-->Y;", "storage://normalized/doc.md", Ct);

        written.Should().Be(CorrectionWrite.Applied);
        var (job, figures) = await ReadAsync(name, id);
        job.Status.Should().Be(ConversionJobStatus.Succeeded);
        figures.Should().ContainSingle(f => f.FigureId == "fig-1").Which.Corrected.Should().BeTrue();
    }

    [Fact]
    public async Task 図が無ければMissingを返し記録しない()
    {
        var (name, id) = await SucceededAsync();
        await using var db = NewDb(name);

        (await new EfConversionJobStore(db)
            .ApplyCorrectionAsync(id, "fig-x", "mermaid", "flowchart LR; X-->Y;", "storage://normalized/doc.md", Ct))
            .Should().Be(CorrectionWrite.Missing);
    }
}
