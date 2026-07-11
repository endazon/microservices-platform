using ConversionService.Worker.Foundation.Jobs;
using ConversionService.Worker.Foundation.Persistence;
using FluentAssertions;
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
}
