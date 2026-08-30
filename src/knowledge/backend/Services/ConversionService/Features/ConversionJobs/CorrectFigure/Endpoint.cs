using ConversionService.Domain;
using Knowledge.Contracts.Dtos;

namespace ConversionService.Features.ConversionJobs.CorrectFigure;

// UC-06, SC-07, IADR-0154: 人手補正の投稿。**Phase 1 は縮退した図に限る**（05_screens:330）。
internal static class CorrectFigureEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/{id:guid}/figures/{figureId}/correction", async (Guid id, string figureId,
            FigureCorrectionRequest request, IFigureCorrectionService corrections, CancellationToken ct) =>
        {
            // IADR-0154 決定 3: 空だけでなく、**コードフェンスを内側から閉じられる入力**も弾く。
            // 保存された本文は再発行されて一般利用者が読むため、ここを通すと文書が壊れる
            // （PR #650 レビュー 2 巡目）。
            if (!FigureMarkdown.IsEmbeddable(request.Language, request.Code))
                return Results.BadRequest(new { error = "invalid_correction" });

            var outcome = await corrections.CorrectAsync(id, figureId, request, ct);
            return outcome.Status switch
            {
                FigureCorrectionStatus.Applied => Results.Ok(new FigureCorrectionResultDto(
                    figureId, outcome.MarkdownUri!, outcome.CorrectedFigures)),
                FigureCorrectionStatus.JobNotFound or FigureCorrectionStatus.FigureNotFound
                    => Results.NotFound(),
                FigureCorrectionStatus.NotCorrectable
                    => Results.Conflict(new { error = "figure_not_correctable" }),
                FigureCorrectionStatus.JobBusy
                    => Results.Conflict(new { error = "job_busy" }),
                // 本文が読めない・埋め込みが見つからない。**補正は保存していない。**
                _ => Results.Conflict(new { error = "body_unavailable" }),
            };
        }).WithName("ConversionJobFigureCorrection");
    }
}
