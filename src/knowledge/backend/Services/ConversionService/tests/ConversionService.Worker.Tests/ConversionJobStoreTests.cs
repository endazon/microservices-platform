using ConversionService.Worker.Foundation.Jobs;
using ConversionService.Worker.Foundation.Persistence;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;

namespace ConversionService.Worker.Tests;

// FR-12, UC-06, SC-07, IADR-0042/IADR-0043: 変換ジョブ読み取りモデル（EF・Postgres 永続化）の状態遷移・
// 絞り込み・人手補正（再変換）を検証する。ここでは EF Core InMemory provider で EfConversionJobStore を検証する。
public class ConversionJobStoreTests
{
    private static EfConversionJobStore NewStore() =>
        new(new ConversionJobDbContext(new DbContextOptionsBuilder<ConversionJobDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options));

    private static RawDocumentFetched Raw(Guid id, string type = "filesystem") =>
        new(id, Guid.NewGuid(), type, "/docs/a.docx", $"storage://{id}/raw",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, ["hr"], DateTimeOffset.UtcNow);

    [Fact]
    public async Task Start_marks_job_processing_with_attempt_one()
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        await store.StartAsync(Raw(id));

        var job = await store.GetAsync(id);
        job.Should().NotBeNull();
        job!.Status.Should().Be(ConversionJobStatus.Processing);
        job.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task Succeed_records_document_and_markdown()
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        await store.StartAsync(Raw(id));
        var docId = Guid.NewGuid();
        await store.SucceedAsync(id, docId, "storage://bucket/a.md");

        var job = (await store.GetAsync(id))!;
        job.Status.Should().Be(ConversionJobStatus.Succeeded);
        job.DocumentId.Should().Be(docId);
        job.MarkdownUri.Should().Be("storage://bucket/a.md");
        job.Error.Should().BeNull();
    }

    [Fact]
    public async Task Fail_records_error()
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        await store.StartAsync(Raw(id));
        await store.FailAsync(id, "pandoc がタイムアウトしました。");

        var job = (await store.GetAsync(id))!;
        job.Status.Should().Be(ConversionJobStatus.Failed);
        job.Error.Should().Be("pandoc がタイムアウトしました。");
    }

    [Fact]
    public async Task List_filters_by_status()
    {
        var store = NewStore();
        var ok = Guid.NewGuid();
        var bad = Guid.NewGuid();
        await store.StartAsync(Raw(ok));
        await store.SucceedAsync(ok, Guid.NewGuid(), "storage://ok.md");
        await store.StartAsync(Raw(bad));
        await store.FailAsync(bad, "失敗");

        (await store.ListAsync(null)).Should().HaveCount(2);
        (await store.ListAsync(ConversionJobStatus.Failed)).Should().ContainSingle(j => j.Id == bad);
        (await store.ListAsync(ConversionJobStatus.Succeeded)).Should().ContainSingle(j => j.Id == ok);
    }

    [Fact]
    public async Task PrepareRetry_requeues_and_returns_original_event()
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        await store.StartAsync(Raw(id));
        await store.FailAsync(id, "失敗");

        var ev = await store.PrepareRetryAsync(id);

        ev.Should().NotBeNull();
        ev!.FetchId.Should().Be(id);
        // 再変換用に原本イベントが再構成される（属性・タグを含む）。
        ev.Attributes.Should().ContainKey("confidentiality");
        ev.Tags.Should().Contain("hr");
        (await store.GetAsync(id))!.Status.Should().Be(ConversionJobStatus.Queued);
    }

    [Fact]
    public async Task PrepareRetry_returns_null_for_unknown_job()
    {
        var store = NewStore();
        (await store.PrepareRetryAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task PrepareRetry_returns_null_for_non_failed_job()
    {
        // UC-06: 人手補正は失敗ジョブのみ。成功済みジョブは再変換せず状態も変えない。
        var store = NewStore();
        var id = Guid.NewGuid();
        await store.StartAsync(Raw(id));
        await store.SucceedAsync(id, Guid.NewGuid(), "storage://ok.md");

        (await store.PrepareRetryAsync(id)).Should().BeNull();
        (await store.GetAsync(id))!.Status.Should().Be(ConversionJobStatus.Succeeded);
    }

    [Fact]
    public async Task Start_again_increments_attempts()
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        await store.StartAsync(Raw(id));
        await store.StartAsync(Raw(id));

        (await store.GetAsync(id))!.Attempts.Should().Be(2);
    }

    // ── FR-12, SC-07（05_screens:324・裁定 Q13）: デッドレター標識と試行上限 ────────────────

    [Fact]
    public async Task Job_carries_max_attempts_and_is_not_dead_lettered_initially()
    {
        // AC-1/AC-2: 試行上限は全ジョブに載る。AC-4: 標識は既定で立たない。
        var store = NewStore();
        var id = Guid.NewGuid();
        await store.StartAsync(Raw(id));

        var job = (await store.GetAsync(id))!;
        job.MaxAttempts.Should().Be(ConversionJobRetryPolicy.MaxAttempts);
        job.DeadLettered.Should().BeFalse();
    }

    [Fact]
    public async Task Fail_without_reaching_attempt_limit_does_not_mark_dead_letter()
    {
        // AC-4: まだ自動再試行の余地がある失敗は failed だが「デッドレター」ではない。
        // 内訳が区別できないと、運用者は「再実行すれば直るもの」と「直らないもの」を見分けられない。
        var store = NewStore();
        var id = Guid.NewGuid();
        await store.StartAsync(Raw(id));
        await store.FailAsync(id, "一時的な失敗", deadLettered: false);

        var job = (await store.GetAsync(id))!;
        job.Status.Should().Be(ConversionJobStatus.Failed);
        job.DeadLettered.Should().BeFalse();
    }

    [Fact]
    public async Task Fail_at_attempt_limit_marks_dead_letter_without_changing_status()
    {
        // AC-6/AC-7: 自動再試行を使い切った失敗＝デッドレター（宛先キュー名ではなく契機で定義する）。
        // AC-3: それでも状態値は failed のままである（4 値モデルを壊さない）。
        var store = NewStore();
        var id = Guid.NewGuid();
        await store.StartAsync(Raw(id));
        await store.FailAsync(id, "本文変換 恒久失敗", deadLettered: true);

        var job = (await store.GetAsync(id))!;
        job.Status.Should().Be(ConversionJobStatus.Failed);
        job.DeadLettered.Should().BeTrue();
    }

    [Fact]
    public async Task Reprocessing_clears_dead_letter_marker()
    {
        // AC-8: 再受信で処理が始まったら標識は落ちる（processing なのにデッドレター、を出さない）。
        var store = NewStore();
        var id = Guid.NewGuid();
        await store.StartAsync(Raw(id));
        await store.FailAsync(id, "恒久失敗", deadLettered: true);
        await store.StartAsync(Raw(id));

        var job = (await store.GetAsync(id))!;
        job.Status.Should().Be(ConversionJobStatus.Processing);
        job.DeadLettered.Should().BeFalse();
    }

    [Fact]
    public async Task PrepareRetry_clears_dead_letter_marker()
    {
        // AC-8: 手動再変換を受け付けた時点で標識は落ちる（queued は「自動では直らない」状態ではない）。
        // AC-5: 試行上限は**自動再試行**の上限であり、手動再変換は回数で縛らない
        //       （05_screens:310「手動再変換の回数上限は設けない」）。試行回数が上限を超えていても受け付ける。
        var store = NewStore();
        var id = Guid.NewGuid();
        for (var i = 0; i < ConversionJobRetryPolicy.MaxAttempts + 1; i++)
            await store.StartAsync(Raw(id));
        await store.FailAsync(id, "恒久失敗", deadLettered: true);

        (await store.GetAsync(id))!.Attempts.Should().BeGreaterThan(ConversionJobRetryPolicy.MaxAttempts);
        (await store.PrepareRetryAsync(id)).Should().NotBeNull();

        var job = (await store.GetAsync(id))!;
        job.Status.Should().Be(ConversionJobStatus.Queued);
        job.DeadLettered.Should().BeFalse();
    }
}
