using ConversionService.Domain;
using ConversionService.Domain.Ports;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;

namespace ConversionService.Infrastructure.Persistence;

// FR-12, UC-06, SC-07, IADR-0042/IADR-0043: 変換ジョブの読み取りモデル。ConversionService はイベント駆動の
// fire-and-forget ワーカーで、これまで変換状況を問い合わせる手段が無かった（失敗はデッドレターのみ）。
// SC-07（変換状況・失敗一覧・人手補正）のため、変換ライフサイクルを記録する。
// IADR-0043: 永続化（Postgres+EF）に伴い非同期 API へ変更（EF I/O は非同期が正道）。
public interface IConversionJobStore
{
    // 変換開始（受信・再試行の都度）。原本イベントは人手補正（再変換）のため保持する。
    Task StartAsync(RawDocumentFetched ev, CancellationToken ct = default);
    // IADR-0154 決定 1: 図の記録も一緒に受け取り、成功のたびに洗い替える。
    // ADR-0070 決定 3 / IADR-0356 (#1192) / [[IADR-0388]] (#1254): hasBody＝原本が本文を持っていたか
    // （`false` は本文なし＝テキスト層の無い PDF で完了したことを表す）。
    // **succeeded の内訳**であり、状態値は 4 値のまま増やさない（DeadLettered と同型）。
    Task SucceedAsync(Guid id, Guid documentId, string markdownUri,
        IReadOnlyList<NormalizedFigure>? figures = null, bool hasBody = true,
        CancellationToken ct = default);
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
    // 🔴 FR-12, #2255: 書く時点のジョブが変換中なら書かない。保存が並行する状態の変化に負けたら投げずに JobChanged を返す。
    Task<CorrectionWrite> ApplyCorrectionAsync(Guid jobId, string figureId, string language, string code,
        string markdownUri, CancellationToken ct = default);
}

// FR-12, UC-06, #2255: 補正の記録の結果。
public enum CorrectionWrite
{
    Applied,
    // ジョブか図が見つからない（記録していない）。
    Missing,
    // 確かめた後にジョブの状態が変わった（再処理が始まった等）。**記録していない。**
    JobChanged,
}

// IADR-0043: EF Core（Postgres）実装。ConversionJobDbContext は scoped のため本ストアも scoped で登録する。
// MassTransit はメッセージ消費ごとに DI スコープを張るため、コンシューマ・エンドポイント双方で解決できる。
public sealed class EfConversionJobStore(ConversionJobDbContext db) : IConversionJobStore
{
    public async Task StartAsync(RawDocumentFetched ev, CancellationToken ct = default)
    {
        // read-modify-write。#2159: 状態が並行トークンなので、読んだ後に状態が変わっていれば保存で例外になる（受け口の再試行で読み直す）。
        // 状態を変えない並行（同じ原本の再配送どうしの attempts++）は照合に掛からず後勝ちのまま（IADR-0043 follow-up）。
        var job = await db.ConversionJobs.FindAsync([ev.FetchId], ct);
        if (job is null)
            db.ConversionJobs.Add(ConversionJob.StartNew(ev));
        else
            job.MarkProcessing(ev);
        await db.SaveChangesAsync(ct);
    }

    public async Task SucceedAsync(Guid id, Guid documentId, string markdownUri,
        IReadOnlyList<NormalizedFigure>? figures = null, bool hasBody = true,
        CancellationToken ct = default)
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
        job.MarkSucceeded(documentId, markdownUri, records, hasBody);
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
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // 🔴 FR-12, UC-06, #2159: **並行する再変換に負けた**（読んだ後に別の再変換が queued へ進めた）。状態は並行トークンなので
            // `UPDATE … WHERE "Status" = 'failed'` が 0 行になった。発行させない（null）。負けた保存は EF の保存点まで巻き戻っている。
            // 負けた変更（queued・補正の破棄）は**このジョブと図の追跡だけ**を外して捨てる —— 残すと同じ確定の中の後続の保存が書き戻す。
            // DbContext 全体（`ChangeTracker.Clear()`）は捨てない: 同じ確定（スコープ）に他の書き込みが載っていれば、それまで消してしまう。
            db.Entry(job).State = EntityState.Detached;
            foreach (var figure in job.Figures) db.Entry(figure).State = EntityState.Detached;
            return null;
        }
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

    public async Task<CorrectionWrite> ApplyCorrectionAsync(Guid jobId, string figureId, string language, string code,
        string markdownUri, CancellationToken ct = default)
    {
        var job = await db.ConversionJobs.Include(j => j.Figures)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);
        var figure = job?.Figures.FirstOrDefault(f => f.FigureId == figureId);
        if (job is null || figure is null) return CorrectionWrite.Missing;
        // 🔴 FR-12, UC-06, #2255 窓 B: 補正の口が最初に確かめてから、ここで読むまでの間に再処理が始まっていれば書かない。
        // 確かめ直さないと、読んだ processing のまま並行トークンの照合も通り、処理中のジョブへ補正を黙って書く。
        if (ConversionJob.IsBusy(job.Status))
            return CorrectionWrite.JobChanged;
        figure.TryApplyCorrection(language, code);
        job.MarkCorrected(markdownUri);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // 🔴 FR-12, UC-06, #2255 窓 A: ここで読んだ後・保存の前に状態が変わった（`UPDATE … WHERE "Status" = @読んだ状態` が 0 行）。
            // 投げると補正の口は 500 になる。負けた変更（補正・本文の参照）は**このジョブと図の追跡だけ**を外して捨てる
            // （#2159 の PrepareRetryAsync と同じ。DbContext 全体は捨てない）。
            db.Entry(job).State = EntityState.Detached;
            foreach (var f in job.Figures) db.Entry(f).State = EntityState.Detached;
            return CorrectionWrite.JobChanged;
        }
        return CorrectionWrite.Applied;
    }
}
