using DashboardService.Domain;
using DashboardService.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Kernel;

namespace DashboardService.Features.KnowledgeHealth.Report;

// FR-10, FR-17, FR-18: 観測値の受け口（指標 1 つ分のスナップショット置換）。
// 生産者は知識グラフ・文書カタログ側のサービスであり、**DB-per-service（ADR-0002）のため
// DashboardService から直接は数えられない**。集計主体を DashboardService に置く判断は
// IADR-0011（業務指標は DashboardService・技術/費用は可観測性スタック）に従う。
//
// ★［2026-08-29 移設 / #443］**`/dashboard/...` から `/internal/...` へ移し、認証を外した**
// （[[IADR-0299]] 決定 4。利用者裁定）。従前は `RequireAuthorization()`（認証済み）だったが、
// **生産者は利用者 JWT を持たない定期処理**であり、`client_credentials` の実装は
// リポジトリ本体に 1 行も無い。`/internal/notifications`（NotificationIngressEndpoints）が
// 同じ制約に対して採った形をそのまま倣う —— 内部 API は OpenAPI にも載せない。
//
// **統制**（定めたもの）: 第一防御は mesh の STRICT mTLS、多層防御としてネットワーク分離
// （内部サービスは host 非公開・Service は既定 ClusterIP・NetworkPolicy 既定拒否）。
// 🔴 **統制が働いていること（測れているもの）は別である。** 機械検査が実際に押さえているのは
// 「compose が host 公開しない」「Helm の Service に type: が現れない」の 2 点だけであり
// （NetworkIsolationTests）、**mTLS が実際に遮断していることは測れていない**。
// 残余リスク（同一ネットワーク内からは無認証で観測値を差し替えられる）は [[IADR-0299]] に
// 受容として記録した。作れるのは**指標名と不透明な鍵の集合だけ**であり、
// **受け口は書き込み専用で既存の観測値を読み出さない**（読み出しは閲覧の GET のみ・ロール限定）。
public static class ReportKnowledgeHealthEndpoint
{
    // 🔴 送信側 GraphService.Infrastructure.ExternalServices.HttpKnowledgeHealthReporter.ObservationsPath と同値。
    // **サービスを跨ぐため定数を共有できない**（サービス間は直接参照しない）。
    // `/internal/notifications` と同じく、**文字列の一致は両側のテストで固定している**。
    public const string ObservationsPath = "/internal/knowledge-health/observations";

    internal static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost(ObservationsPath, async (
            KnowledgeHealthReportRequest req, IValidator<KnowledgeHealthReportRequest> validator,
            DashboardDbContext db, CancellationToken ct) =>
        {
            // FR-10, FR-17, FR-18 / IADR-0371 決定 2・4 / IADR-0376: 入力検証（FluentValidation）の
            // 失敗を Kernel の `Result` で表し、**HTTP への写像は 1 度だけ行う**
            // （計画 ADR-0030 §決定「ProblemDetails 変換は API 層」/ ADR-0041 §結果）。
            // 判定の順序は移送前のガード節と同じ（指標 → しきい値）であり、
            // 返す状態コードも本文も変わらない。
            var gate = Validate(validator, req);
            if (gate.IsFailure)
            {
                // ErrorKind で HTTP を決める。**分岐は 1 箇所に閉じる。**
                return gate.Error.Kind == ErrorKind.Validation
                    ? Results.BadRequest(new { error = gate.Error.Message })
                    : Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            var indicator = KnowledgeHealthIndicators.Normalize(req.Indicator);
            var observedAt = DateTimeOffset.UtcNow;

            // スナップショット置換: 当該指標の既存行を落としてから差し替える。
            var stale = await db.KnowledgeHealthObservations
                .Where(o => o.Indicator == indicator)
                .ToListAsync(ct);
            db.KnowledgeHealthObservations.RemoveRange(stale);

            var observations = (req.Observations ?? [])
                .Where(o => !string.IsNullOrWhiteSpace(o.SubjectKey))
                .Select(o => KnowledgeHealthObservation.Create(indicator, o.SubjectKey, o.DocScope, observedAt))
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
            return Results.Accepted(value: new { indicator, accepted = observations.Count });
        }).WithName("ReportKnowledgeHealth")
          .ExcludeFromDescription()
          .Produces(StatusCodes.Status202Accepted);
    }

    // FR-10 / IADR-0371 決定 2: 入力規則の判定。
    // **規則そのものは `ReportKnowledgeHealthValidator` が持つ。**
    //
    // 🔴 **`Errors[0]` を採る。** FluentValidation は既定で全規則を走らせるため、
    // 移送前の「最初の違反で 400 を返す」と同じ本文にするには最初の失敗を採るしかない。
    // 規則の宣言順が応答の契約の一部になっている（同 Validator のコメントを参照）。
    private static Result Validate(IValidator<KnowledgeHealthReportRequest> validator,
        KnowledgeHealthReportRequest req)
    {
        var result = validator.Validate(req);
        return result.IsValid
            ? Result.Success()
            : Result.Failure(Error.Validation(
                "dashboard.knowledge-health.invalid", result.Errors[0].ErrorMessage));
    }
}
