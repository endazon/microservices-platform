using DashboardService.Domain;
using DashboardService.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Kernel;

namespace DashboardService.Features.KnowledgeHealth.Report;

// FR-10, FR-17, FR-18, SC-10, ADR-0002, ADR-0006, ADR-0029, ADR-0030, ADR-0065 決定 2, ADR-0075,
// [[IADR-0265]], [[IADR-0299]], [[IADR-0353]], [[IADR-0371]] 決定 2, [[IADR-0379]], [[IADR-0389]],
// [[IADR-0408]] (#1255): 観測値の受け口の**本体**。
//
// 🔴 **REST の端点と gRPC の rpc が同じ関数を通る。** 輸送を 2 つに増やすときに**本体も 2 つにすると、
// 片方だけが直る**（置換の順序・しきい値の削除・件数の数え方はどれも「静かに違う」形で割れる）。
// [[IADR-0397]] の `EmbedUseCase`・[[IADR-0400]] の `CompletionUseCase`・[[IADR-0402]] の
// `DocumentReadUseCase` と同じ形である。
//
// `ADR-0065` 決定 2 の適用としては「1 操作の実体」なので、その操作のフォルダ（`Report/`）に置く。
//
// 🔴 **HTTP / gRPC の言葉をここへ持ち込まない。** 失敗は Kernel の `Result` で返し、
// 状態コード・gRPC status への写像は輸送側（端点 / GrpcService）が 1 度だけ行う
// （計画 `ADR-0030` §決定・`ADR-0041` §結果）。
public sealed class ReportKnowledgeHealthUseCase(
    DashboardDbContext db,
    IValidator<KnowledgeHealthReportRequest> validator)
{
    /// <summary>
    /// 指標 1 つ分の観測値をスナップショット置換する。戻り値は (正規化後の指標名, 受理件数)。
    /// </summary>
    public async Task<Result<ReportKnowledgeHealthOutcome>> ExecuteAsync(
        KnowledgeHealthReportRequest req, CancellationToken ct)
    {
        var gate = Validate(req);
        if (gate.IsFailure)
            return Result<ReportKnowledgeHealthOutcome>.Failure(gate.Error);

        var indicator = KnowledgeHealthIndicators.Normalize(req.Indicator);
        var observedAt = DateTimeOffset.UtcNow;

        // スナップショット置換: 当該指標の既存行を落としてから差し替える。
        var stale = await db.KnowledgeHealthObservations
            .Where(o => o.Indicator == indicator)
            .ToListAsync(ct);
        db.KnowledgeHealthObservations.RemoveRange(stale);

        var observations = (req.Observations ?? [])
            .Where(o => !string.IsNullOrWhiteSpace(o.SubjectKey))
            .Select(o => KnowledgeHealthObservation.Create(
                indicator, o.SubjectKey, o.DocScope, observedAt, o.Dimension))
            .ToList();
        db.KnowledgeHealthObservations.AddRange(observations);

        // planning#494 決定 3 (#1186): 現在のしきい値も**スナップショットとして置き換える**。
        // 🔴 **添えられていなければ行を消す。** 残すと、生産者がしきい値の要らない指標へ
        // 変わった後も古い日数が画面に出続ける（観測値を全量置換するのと同じ理由）。
        var threshold = await db.KnowledgeHealthIndicatorThresholds
            .FirstOrDefaultAsync(t => t.Indicator == indicator, ct);
        if (req.ThresholdDays is { } days)
        {
            if (threshold is null)
                db.KnowledgeHealthIndicatorThresholds.Add(
                    KnowledgeHealthIndicatorThreshold.Create(indicator, days, observedAt));
            else
                threshold.Update(days, observedAt);
        }
        else if (threshold is not null)
        {
            db.KnowledgeHealthIndicatorThresholds.Remove(threshold);
        }

        await db.SaveChangesAsync(ct);

        // **受け付けた件数だけを返す**（個人資料を含み得る生の値であり、ここは集計面ではない）。
        return Result<ReportKnowledgeHealthOutcome>.Success(
            new ReportKnowledgeHealthOutcome(indicator, observations.Count));
    }

    // FR-10 / IADR-0371 決定 2: 入力規則の判定。
    // **規則そのものは `ReportKnowledgeHealthValidator` が持つ。**
    //
    // 🔴 **`Errors[0]` を採る。** FluentValidation は既定で全規則を走らせるため、
    // 移送前の「最初の違反で 400 を返す」と同じ本文にするには最初の失敗を採るしかない。
    // 規則の宣言順が応答の契約の一部になっている（同 Validator のコメントを参照）。
    private Result Validate(KnowledgeHealthReportRequest req)
    {
        var result = validator.Validate(req);
        return result.IsValid
            ? Result.Success()
            : Result.Failure(Error.Validation(
                "dashboard.knowledge-health.invalid", result.Errors[0].ErrorMessage));
    }
}

/// <summary>受理の結果。REST の 202 本文・gRPC の <c>ReportResponse</c> と同形である。</summary>
public readonly record struct ReportKnowledgeHealthOutcome(string Indicator, int Accepted);
