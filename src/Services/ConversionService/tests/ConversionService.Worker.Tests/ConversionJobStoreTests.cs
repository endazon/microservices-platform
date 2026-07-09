using ConversionService.Worker.Foundation.Jobs;
using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Dtos;
using KnowledgePlatform.Shared.Contracts.Events;

namespace ConversionService.Worker.Tests;

// FR-12, UC-06, SC-07, IADR-0042: 変換ジョブ読み取りモデル（インメモリ）の状態遷移・絞り込み・
// 人手補正（再変換）を検証する。
public class ConversionJobStoreTests
{
    private static RawDocumentFetched Raw(Guid id, string type = "filesystem") =>
        new(id, Guid.NewGuid(), type, "/docs/a.docx", $"storage://{id}/raw",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, ["hr"], DateTimeOffset.UtcNow);

    [Fact]
    public void Start_marks_job_processing_with_attempt_one()
    {
        var store = new InMemoryConversionJobStore();
        var id = Guid.NewGuid();
        store.Start(Raw(id));

        var job = store.Get(id);
        job.Should().NotBeNull();
        job!.Status.Should().Be(ConversionJobStatus.Processing);
        job.Attempts.Should().Be(1);
    }

    [Fact]
    public void Succeed_records_document_and_markdown()
    {
        var store = new InMemoryConversionJobStore();
        var id = Guid.NewGuid();
        store.Start(Raw(id));
        var docId = Guid.NewGuid();
        store.Succeed(id, docId, "storage://bucket/a.md");

        var job = store.Get(id)!;
        job.Status.Should().Be(ConversionJobStatus.Succeeded);
        job.DocumentId.Should().Be(docId);
        job.MarkdownUri.Should().Be("storage://bucket/a.md");
        job.Error.Should().BeNull();
    }

    [Fact]
    public void Fail_records_error()
    {
        var store = new InMemoryConversionJobStore();
        var id = Guid.NewGuid();
        store.Start(Raw(id));
        store.Fail(id, "pandoc がタイムアウトしました。");

        var job = store.Get(id)!;
        job.Status.Should().Be(ConversionJobStatus.Failed);
        job.Error.Should().Be("pandoc がタイムアウトしました。");
    }

    [Fact]
    public void List_filters_by_status()
    {
        var store = new InMemoryConversionJobStore();
        var ok = Guid.NewGuid();
        var bad = Guid.NewGuid();
        store.Start(Raw(ok));
        store.Succeed(ok, Guid.NewGuid(), "storage://ok.md");
        store.Start(Raw(bad));
        store.Fail(bad, "失敗");

        store.List(null).Should().HaveCount(2);
        store.List(ConversionJobStatus.Failed).Should().ContainSingle(j => j.Id == bad);
        store.List(ConversionJobStatus.Succeeded).Should().ContainSingle(j => j.Id == ok);
    }

    [Fact]
    public void PrepareRetry_requeues_and_returns_original_event()
    {
        var store = new InMemoryConversionJobStore();
        var id = Guid.NewGuid();
        store.Start(Raw(id));
        store.Fail(id, "失敗");

        var ev = store.PrepareRetry(id);

        ev.Should().NotBeNull();
        ev!.FetchId.Should().Be(id);
        store.Get(id)!.Status.Should().Be(ConversionJobStatus.Queued);
    }

    [Fact]
    public void PrepareRetry_returns_null_for_unknown_job()
    {
        var store = new InMemoryConversionJobStore();
        store.PrepareRetry(Guid.NewGuid()).Should().BeNull();
    }

    [Fact]
    public void Start_again_increments_attempts()
    {
        var store = new InMemoryConversionJobStore();
        var id = Guid.NewGuid();
        store.Start(Raw(id));
        store.Start(Raw(id));

        store.Get(id)!.Attempts.Should().Be(2);
    }
}
