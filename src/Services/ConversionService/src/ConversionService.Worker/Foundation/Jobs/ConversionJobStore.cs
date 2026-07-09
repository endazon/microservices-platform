using System.Collections.Concurrent;
using KnowledgePlatform.Shared.Contracts.Dtos;
using KnowledgePlatform.Shared.Contracts.Events;

namespace ConversionService.Worker.Foundation.Jobs;

// FR-12, UC-06, SC-07, IADR-0042: 変換ジョブの読み取りモデル。ConversionService はイベント駆動の
// fire-and-forget ワーカーで、これまで変換状況を問い合わせる手段が無かった（失敗はデッドレターのみ）。
// SC-07（変換状況・失敗一覧・人手補正）のため、変換ライフサイクルを記録する軽量なストアを設ける。
// MVP はインメモリ（プロセス内）。永続化・複数インスタンス共有は follow-up（IADR-0042 参照）。
public interface IConversionJobStore
{
    // 変換開始（受信・再試行の都度）。原本イベントは人手補正（再変換）のため保持する。
    void Start(RawDocumentFetched ev);
    void Succeed(Guid id, Guid documentId, string markdownUri);
    void Fail(Guid id, string error);
    IReadOnlyList<ConversionJobDto> List(string? status);
    ConversionJobDto? Get(Guid id);
    // 人手補正: 失敗ジョブを queued に戻し、再変換用の原本イベントを返す。
    // 未知の id・失敗以外の状態（processing/succeeded/queued）は null（＝再変換不可）。
    RawDocumentFetched? PrepareRetry(Guid id);
}

public sealed class InMemoryConversionJobStore : IConversionJobStore
{
    private sealed class Entry
    {
        public required RawDocumentFetched Event { get; set; }
        public string Status { get; set; } = ConversionJobStatus.Queued;
        public string? Error { get; set; }
        public Guid? DocumentId { get; set; }
        public string? MarkdownUri { get; set; }
        public int Attempts { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    private readonly ConcurrentDictionary<Guid, Entry> _jobs = new();

    public void Start(RawDocumentFetched ev)
    {
        _jobs.AddOrUpdate(
            ev.FetchId,
            _ => new Entry
            {
                Event = ev,
                Status = ConversionJobStatus.Processing,
                Attempts = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            (_, existing) =>
            {
                lock (existing)
                {
                    existing.Event = ev;
                    existing.Status = ConversionJobStatus.Processing;
                    existing.Error = null;
                    existing.Attempts++;
                    existing.UpdatedAt = DateTimeOffset.UtcNow;
                }
                return existing;
            });
    }

    public void Succeed(Guid id, Guid documentId, string markdownUri)
    {
        if (!_jobs.TryGetValue(id, out var e)) return;
        lock (e)
        {
            e.Status = ConversionJobStatus.Succeeded;
            e.Error = null;
            e.DocumentId = documentId;
            e.MarkdownUri = markdownUri;
            e.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public void Fail(Guid id, string error)
    {
        if (!_jobs.TryGetValue(id, out var e)) return;
        lock (e)
        {
            e.Status = ConversionJobStatus.Failed;
            e.Error = error;
            e.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public IReadOnlyList<ConversionJobDto> List(string? status)
    {
        IEnumerable<Entry> jobs = _jobs.Values;
        if (!string.IsNullOrWhiteSpace(status))
            jobs = jobs.Where(e => string.Equals(e.Status, status, StringComparison.OrdinalIgnoreCase));
        return jobs
            .OrderByDescending(e => e.UpdatedAt)
            .Select(ToDto)
            .ToList();
    }

    public ConversionJobDto? Get(Guid id) =>
        _jobs.TryGetValue(id, out var e) ? ToDto(e) : null;

    public RawDocumentFetched? PrepareRetry(Guid id)
    {
        if (!_jobs.TryGetValue(id, out var e)) return null;
        lock (e)
        {
            // UC-06: 人手補正は失敗ジョブに限る。処理中の二重発行・成功済みの不要な再処理を防ぐ。
            if (e.Status != ConversionJobStatus.Failed) return null;
            e.Status = ConversionJobStatus.Queued;
            e.Error = null;
            e.UpdatedAt = DateTimeOffset.UtcNow;
            return e.Event;
        }
    }

    private static ConversionJobDto ToDto(Entry e)
    {
        lock (e)
        {
            return new ConversionJobDto(
                e.Event.FetchId, e.Event.SourceId, e.Event.SourceType, e.Event.OriginalPath,
                e.Status, e.Error, e.DocumentId, e.MarkdownUri, e.Attempts, e.CreatedAt, e.UpdatedAt);
        }
    }
}
