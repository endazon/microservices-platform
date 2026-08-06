using Knowledge.Contracts.Dtos;
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

    // FR-12, SC-07, IADR-0136: 再試行を使い切って <queue>_error へ送られたか（failed の内訳）。
    // 導出（Attempts >= 上限）にしないのは、Attempts が手動再変換をまたいで累積するためである。
    public bool DeadLettered { get; private set; }

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
    // SC-07: 処理を再開したらデッドレター標識は落とす（「processing なのにデッドレター」を出さない）。
    public void MarkProcessing(RawDocumentFetched ev)
    {
        Status = ConversionJobStatus.Processing;
        Error = null;
        DeadLettered = false;
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

    // SC-07: deadLettered は「この失敗で自動再試行を使い切った（＝<queue>_error へ送られる）」ことを
    // コンシューマから受け取る。再試行の余地が残る失敗では立たない（failed の内訳を区別するため）。
    public void MarkFailed(string error, bool deadLettered = false)
    {
        Status = ConversionJobStatus.Failed;
        Error = error;
        DeadLettered = deadLettered;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    // UC-06: 人手補正は失敗ジョブに限る。失敗以外（processing/succeeded/queued）は再変換不可。
    // SC-07: 再変換を受け付けた時点でデッドレター標識は落とす（queued は「自動では直らない」状態ではない）。
    public bool TryRequeue()
    {
        if (Status != ConversionJobStatus.Failed) return false;
        Status = ConversionJobStatus.Queued;
        Error = null;
        DeadLettered = false;
        UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    // 再変換用に原本イベントを再構成する（防御的コピー）。
    public RawDocumentFetched ToEvent() =>
        new(Id, SourceId, SourceType, OriginalPath, StorageUri, ContentType,
            new Dictionary<string, string>(Attributes), [.. Tags], FetchedAt);

    // SC-07: 試行上限は全ジョブ共通の設定値であり、画面がしきい値を推測しなくて済むよう毎行へ射影する。
    public ConversionJobDto ToDto() =>
        new(Id, SourceId, SourceType, OriginalPath, Status, Error, DocumentId, MarkdownUri,
            Attempts, CreatedAt, UpdatedAt, DeadLettered, ConversionJobRetryPolicy.MaxAttempts);

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
