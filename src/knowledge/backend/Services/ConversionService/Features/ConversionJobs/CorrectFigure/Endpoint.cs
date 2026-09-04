using ConversionService.Domain;
using FluentValidation;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Kernel;

namespace ConversionService.Features.ConversionJobs.CorrectFigure;

// UC-06, SC-07, IADR-0154: 人手補正の投稿。**Phase 1 は縮退した図に限る**（05_screens:330）。
internal static class CorrectFigureEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/{id:guid}/figures/{figureId}/correction", async (Guid id, string figureId,
            FigureCorrectionRequest request, IValidator<FigureCorrectionRequest> validator,
            IFigureCorrectionService corrections, CancellationToken ct) =>
        {
            // UC-06 / IADR-0154 決定 3 / IADR-0371 決定 2・4 / IADR-0377:
            // 空だけでなく、**コードフェンスを内側から閉じられる入力**も弾く。
            // 保存された本文は再発行されて一般利用者が読むため、ここを通すと文書が壊れる
            // （PR #650 レビュー 2 巡目）。規則は `FigureCorrectionValidator` が持つ。
            //
            // 失敗は Kernel の `Result` で表し、**HTTP への写像は 1 度だけ行う**
            // （計画 ADR-0030 §決定「ProblemDetails 変換は API 層」/ ADR-0041 §結果）。
            var gate = Validate(validator, request);
            if (gate.IsFailure)
            {
                // ErrorKind で HTTP を決める。**分岐は 1 箇所に閉じる。**
                return gate.Error.Kind == ErrorKind.Validation
                    ? Results.BadRequest(new { error = gate.Error.Message })
                    : Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

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

    // UC-06 / IADR-0371 決定 2: 入力規則の判定。**規則そのものは `FigureCorrectionValidator` が持つ。**
    //
    // 🔴 **`Errors[0]` を採る。** FluentValidation は既定で全規則を走らせるため、
    // 移送前の「最初の違反で 400 を返す」と同じ本文にするには最初の失敗を採るしかない。
    private static Result Validate(IValidator<FigureCorrectionRequest> validator,
        FigureCorrectionRequest request)
    {
        var result = validator.Validate(request);
        return result.IsValid
            ? Result.Success()
            : Result.Failure(Error.Validation(
                "conversion.figure-correction.invalid", result.Errors[0].ErrorMessage));
    }
}
