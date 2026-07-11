using ConversionService.Worker.Foundation.Persistence;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;

namespace ConversionService.Worker.Foundation.Jobs;

// FR-12, UC-06, SC-07, IADR-0042/IADR-0043: 変換ジョブの読み取りモデル。ConversionService はイベント駆動の
// fire-and-forget ワーカーで、これまで変換状況を問い合わせる手段が無かった（失敗はデッドレターのみ）。
// SC-07（変換状況・失敗一覧・人手補正）のため、変換ライフサイクルを記録する。
// IADR-0043: 永続化（Postgres+EF）に伴い非同期 API へ変更（EF I/O は非同期が正道）。
public interface IConversionJobStore
{
    // 変換開始（受信・再試行の都度）。原本イベントは人手補正（再変換）のため保持する。
    Task StartAsync(RawDocumentFetched ev, CancellationToken ct = default);
    Task SucceedAsync(Guid id, Guid documentId, string markdownUri, CancellationToken ct = default);
    Task FailAsync(Guid id, string error, CancellationToken ct = default);
    Task<IReadOnlyList<ConversionJobDto>> ListAsync(string? status, CancellationToken ct = default);
    Task<ConversionJobDto?> GetAsync(Guid id, CancellationToken ct = default);
    // 人手補正: 失敗ジョブを queued に戻し、再変換用の原本イベントを返す。
    // 未知の id・失敗以外の状態（processing/succeeded/queued）は null（＝再変換不可）。
    Task<RawDocumentFetched?> PrepareRetryAsync(Guid id, CancellationToken ct = default);
}

// IADR-0043: EF Core（Postgres）実装。ConversionJobDbContext は scoped のため本ストアも scoped で登録する。
// MassTransit はメッセージ消費ごとに DI スコープを張るため、コンシューマ・エンドポイント双方で解決できる。
public sealed class EfConversionJobStore(ConversionJobDbContext db) : IConversionJobStore
{
    public async Task StartAsync(RawDocumentFetched ev, CancellationToken ct = default)
    {
        // 単一インスタンス（dev）前提の read-modify-write。水平スケール時の並行性は IADR-0043 follow-up。
        var job = await db.ConversionJobs.FindAsync([ev.FetchId], ct);
        if (job is null)
            db.ConversionJobs.Add(ConversionJob.StartNew(ev));
        else
            job.MarkProcessing(ev);
        await db.SaveChangesAsync(ct);
    }

    public async Task SucceedAsync(Guid id, Guid documentId, string markdownUri, CancellationToken ct = default)
    {
        var job = await db.ConversionJobs.FindAsync([id], ct);
        if (job is null) return;
        job.MarkSucceeded(documentId, markdownUri);
        await db.SaveChangesAsync(ct);
    }

    public async Task FailAsync(Guid id, string error, CancellationToken ct = default)
    {
        var job = await db.ConversionJobs.FindAsync([id], ct);
        if (job is null) return;
        job.MarkFailed(error);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ConversionJobDto>> ListAsync(string? status, CancellationToken ct = default)
    {
        var query = db.ConversionJobs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))
        {
            // 保存値は ConversionJobStatus の正規化済み小文字。入力を小文字化して等価比較する。
            var normalized = status.ToLowerInvariant();
            query = query.Where(j => j.Status == normalized);
        }
        var jobs = await query.OrderByDescending(j => j.UpdatedAt).ToListAsync(ct);
        return jobs.Select(j => j.ToDto()).ToList();
    }

    public async Task<ConversionJobDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var job = await db.ConversionJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        return job?.ToDto();
    }

    public async Task<RawDocumentFetched?> PrepareRetryAsync(Guid id, CancellationToken ct = default)
    {
        var job = await db.ConversionJobs.FindAsync([id], ct);
        if (job is null) return null;
        // UC-06: 失敗ジョブのみ再変換。処理中の二重発行・成功済みの不要な再処理を防ぐ。
        if (!job.TryRequeue()) return null;
        await db.SaveChangesAsync(ct);
        return job.ToEvent();
    }
}
