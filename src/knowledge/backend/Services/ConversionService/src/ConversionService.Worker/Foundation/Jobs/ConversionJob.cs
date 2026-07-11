using KnowledgePlatform.Shared.Contracts.Dtos;
using Knowledge.Contracts.Events;

namespace ConversionService.Worker.Foundation.Jobs;

// FR-12, UC-06, SC-07, IADR-0042/IADR-0043: 変換ジョブの読み取りモデル（永続化エンティティ）。
// 変換コンシューマが受信・成功・失敗の各ライフサイクルを記録する。id は原本取得イベントの FetchId。
// 再変換（人手補正）で原本イベント RawDocumentFetched を再構成できるよう原本項目も保持する。
public class ConversionJob
{
    public Guid Id { get; private set; } // = RawDocumentFetched.FetchId
    public Guid SourceId { get; private set; }
    public string SourceType { get; private set; } = string.Empty;
    public string OriginalPath { get; private set; } = string.Empty;
    public string Status { get; private set; } = ConversionJobStatus.Queued;
    public string? Error { get; private set; }
    public Guid? DocumentId { get; private set; }
    public string? MarkdownUri { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    // 再変換のための原本イベント項目（RawDocumentFetched の再構成に用いる。DTO には射影しない）。
    public string StorageUri { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public Dictionary<string, string> Attributes { get; private set; } = [];
    public List<string> Tags { get; private set; } = [];
    public DateTimeOffset FetchedAt { get; private set; }

    private ConversionJob() { }

    // 初回受信：processing・attempts=1 で新規作成。
    public static ConversionJob StartNew(RawDocumentFetched ev)
    {
        var now = DateTimeOffset.UtcNow;
        var job = new ConversionJob
        {
            Id = ev.FetchId,
            Status = ConversionJobStatus.Processing,
            Attempts = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        job.ApplyEvent(ev);
        return job;
    }

    // 再受信・再試行の都度：processing へ・attempts++・エラー消去・原本を更新。
    public void MarkProcessing(RawDocumentFetched ev)
    {
        Status = ConversionJobStatus.Processing;
        Error = null;
        Attempts++;
        UpdatedAt = DateTimeOffset.UtcNow;
        ApplyEvent(ev);
    }

    public void MarkSucceeded(Guid documentId, string markdownUri)
    {
        Status = ConversionJobStatus.Succeeded;
        Error = null;
        DocumentId = documentId;
        MarkdownUri = markdownUri;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void MarkFailed(string error)
    {
        Status = ConversionJobStatus.Failed;
        Error = error;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    // UC-06: 人手補正は失敗ジョブに限る。失敗以外（processing/succeeded/queued）は再変換不可。
    public bool TryRequeue()
    {
        if (Status != ConversionJobStatus.Failed) return false;
        Status = ConversionJobStatus.Queued;
        Error = null;
        UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    // 再変換用に原本イベントを再構成する（防御的コピー）。
    public RawDocumentFetched ToEvent() =>
        new(Id, SourceId, SourceType, OriginalPath, StorageUri, ContentType,
            new Dictionary<string, string>(Attributes), [.. Tags], FetchedAt);

    public ConversionJobDto ToDto() =>
        new(Id, SourceId, SourceType, OriginalPath, Status, Error, DocumentId, MarkdownUri,
            Attempts, CreatedAt, UpdatedAt);

    private void ApplyEvent(RawDocumentFetched ev)
    {
        SourceId = ev.SourceId;
        SourceType = ev.SourceType;
        OriginalPath = ev.OriginalPath;
        StorageUri = ev.StorageUri;
        ContentType = ev.ContentType;
        Attributes = new Dictionary<string, string>(ev.Attributes);
        Tags = [.. ev.Tags];
        FetchedAt = ev.FetchedAt;
    }
}
