using ConversionService.Worker.Domain;
using ConversionService.Worker.Domain.Ports;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;

namespace ConversionService.Worker.Infrastructure.Persistence;

// FR-12, UC-06, SC-07, IADR-0042/IADR-0043: 変換ジョブの読み取りモデル。ConversionService はイベント駆動の
// fire-and-forget ワーカーで、これまで変換状況を問い合わせる手段が無かった（失敗はデッドレターのみ）。
// SC-07（変換状況・失敗一覧・人手補正）のため、変換ライフサイクルを記録する。
// IADR-0043: 永続化（Postgres+EF）に伴い非同期 API へ変更（EF I/O は非同期が正道）。
public interface IConversionJobStore
{
    // 変換開始（受信・再試行の都度）。原本イベントは人手補正（再変換）のため保持する。
    Task StartAsync(RawDocumentFetched ev, CancellationToken ct = default);
    // IADR-0154 決定 1: 図の記録も一緒に受け取り、成功のたびに洗い替える。
    Task SucceedAsync(Guid id, Guid documentId, string markdownUri,
        IReadOnlyList<NormalizedFigure>? figures = null, CancellationToken ct = default);
    // SC-07 / ADR-0053 決定 2: deadLettered＝この失敗で**自動再試行を使い切ったか**。
    // 🔴 **デッドレターの宛先キュー名で説明しない。** 宛先はトランスポートの都合で変わる
    // （Wolverine の既定は共有 1 本。IADR-0245 決定 9）。**契機は業務上の事実であり、
    // 意味としてトランスポートに依存しない**ため、移行の前後で同じことを指す。
    Task FailAsync(Guid id, string error, bool deadLettered = false, CancellationToken ct = default);
    Task<IReadOnlyList<ConversionJobDto>> ListAsync(string? status, CancellationToken ct = default);
    Task<ConversionJobDto?> GetAsync(Guid id, CancellationToken ct = default);
    // 再変換: 失敗ジョブを queued に戻し、再変換用の原本イベントを返す。
    // 未知の id・失敗以外の状態（processing/succeeded/queued）は null（＝再変換不可）。
    // IADR-0154 決定 4: 補正のあるジョブは discardCorrections を明示しない限り再変換しない
    // （補正版を正とする。05_screens:333）。破棄が要るときは呼び出し側が 409 で確認を求める。
    Task<RawDocumentFetched?> PrepareRetryAsync(Guid id, bool discardCorrections = false,
        CancellationToken ct = default);

    // UC-06, IADR-0154: 人手補正 Phase 1 —— 図の一覧（未知の id は null）。
    Task<IReadOnlyList<ConversionFigureDto>?> ListFiguresAsync(Guid jobId, CancellationToken ct = default);

    // UC-06, IADR-0154: 図 1 つの取得（本文の置換に要る ImageUri を含む）。
    Task<ConversionFigureDto?> GetFigureAsync(Guid jobId, string figureId, CancellationToken ct = default);

    // UC-06, IADR-0154 決定 3: 原本イベントの再構成（ABAC 属性・タグを含む）。
    // 人手補正が DocumentNormalized を再発行するときに使う。**属性を空で再発行してはならない**
    // ——取り込み側は属性から機密区分を読むため、落とすと文書の可視範囲が変わる。
    Task<RawDocumentFetched?> GetSourceEventAsync(Guid id, CancellationToken ct = default);

    // UC-06, IADR-0154: 補正を記録し、更新後の本文 URI をジョブへ反映する。
    Task ApplyCorrectionAsync(Guid jobId, string figureId, string language, string code,
        string markdownUri, CancellationToken ct = default);
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

    public async Task SucceedAsync(Guid id, Guid documentId, string markdownUri,
        IReadOnlyList<NormalizedFigure>? figures = null, CancellationToken ct = default)
    {
        // IADR-0154 決定 1: 図を洗い替えるため、ここだけは Figures を読み込んだうえで操作する。
        var job = await db.ConversionJobs.Include(j => j.Figures).FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null) return;
        var records = figures?
            .Select(f => ConversionJobFigure.Record(id, f.FigureId, f.Coded, f.Language, f.Code,
                f.ImageUri, f.ImageContentType, f.Caption))
            .ToList();
        // 洗い替えでは古い行を明示的に削除する（Clear() だけでは孤児として残る構成があるため）。
        if (records is not null && job.Figures.Count > 0)
            db.ConversionJobFigures.RemoveRange(job.Figures);
        job.MarkSucceeded(documentId, markdownUri, records);
        await db.SaveChangesAsync(ct);
    }

    public async Task FailAsync(Guid id, string error, bool deadLettered = false, CancellationToken ct = default)
    {
        var job = await db.ConversionJobs.FindAsync([id], ct);
        if (job is null) return;
        job.MarkFailed(error, deadLettered);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ConversionJobDto>> ListAsync(string? status, CancellationToken ct = default)
    {
        // IADR-0127: 一覧の各行が図の内訳・「補正あり」を持つため、図を同伴して読む。
        // Include の戻り値型（IIncludableQueryable）のまま Where を代入できないので明示的に型を置く。
        IQueryable<ConversionJob> query = db.ConversionJobs.AsNoTracking().Include(j => j.Figures);
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
        var job = await db.ConversionJobs.AsNoTracking().Include(j => j.Figures)
            .FirstOrDefaultAsync(j => j.Id == id, ct);
        return job?.ToDto();
    }

    public async Task<RawDocumentFetched?> PrepareRetryAsync(Guid id, bool discardCorrections = false,
        CancellationToken ct = default)
    {
        var job = await db.ConversionJobs.Include(j => j.Figures).FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null) return null;
        // IADR-0154 決定 4: 補正版を正とする。確認なしに補正を破棄する再変換は通さない。
        // 呼び出し側は 409 corrections_would_be_lost を返して明示確認を求める。
        if (!discardCorrections && job.CorrectedFigureCount > 0) return null;
        // UC-06: 失敗ジョブのみ再変換。処理中の二重発行・成功済みの不要な再処理を防ぐ。
        if (!job.TryRequeue()) return null;
        // 破棄が確認済みなら補正を落とす（再変換は図を作り直すため、残すと古い補正が復活して見える）。
        foreach (var figure in job.Figures) figure.DiscardCorrection();
        await db.SaveChangesAsync(ct);
        return job.ToEvent();
    }

    public async Task<IReadOnlyList<ConversionFigureDto>?> ListFiguresAsync(Guid jobId,
        CancellationToken ct = default)
    {
        var job = await db.ConversionJobs.AsNoTracking().Include(j => j.Figures)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return null;
        return job.Figures.OrderBy(f => f.FigureId).Select(f => f.ToDto()).ToList();
    }

    public async Task<ConversionFigureDto?> GetFigureAsync(Guid jobId, string figureId,
        CancellationToken ct = default)
    {
        var figure = await db.ConversionJobFigures.AsNoTracking()
            .FirstOrDefaultAsync(f => f.JobId == jobId && f.FigureId == figureId, ct);
        return figure?.ToDto();
    }

    public async Task<RawDocumentFetched?> GetSourceEventAsync(Guid id, CancellationToken ct = default)
    {
        var job = await db.ConversionJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        return job?.ToEvent();
    }

    public async Task ApplyCorrectionAsync(Guid jobId, string figureId, string language, string code,
        string markdownUri, CancellationToken ct = default)
    {
        var job = await db.ConversionJobs.Include(j => j.Figures)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);
        var figure = job?.Figures.FirstOrDefault(f => f.FigureId == figureId);
        if (job is null || figure is null) return;
        figure.TryApplyCorrection(language, code);
        job.MarkCorrected(markdownUri);
        await db.SaveChangesAsync(ct);
    }
}
