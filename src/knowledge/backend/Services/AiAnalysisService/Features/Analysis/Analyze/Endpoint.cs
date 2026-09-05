using AiAnalysisService.Domain.Ports;
using FluentValidation;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Kernel;

namespace AiAnalysisService.Features.Analysis.Analyze;

// FR-07, UC-02: 指定データ範囲での分析・比較・抽出。
internal static class AnalyzeEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/analyze", async (AnalysisTaskRequest req, IValidator<AnalysisTaskRequest> validator,
            IRagOrchestrator rag, HttpContext http) =>
        {
            // FR-07 / IADR-0371 決定 2・4 / IADR-0393: 入力検証（FluentValidation）の失敗を
            // Kernel の `Result` で表し、**HTTP への写像は 1 度だけ行う**
            // （計画 ADR-0030 §決定「ProblemDetails 変換は API 層」/ ADR-0041 §結果）。
            // 返す状態コードも本文も移送前と変わらない。
            var gate = Validate(validator, req);
            if (gate.IsFailure)
            {
                // ErrorKind で HTTP を決める。**分岐は 1 箇所に閉じる。**
                return gate.Error.Kind == ErrorKind.Validation
                    ? Results.BadRequest(new { error = gate.Error.Message })
                    : Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            // FR-05: JWT から利用者を特定し、権限解決（範囲は権限を広げない）
            var userId = http.User.Identity?.Name ?? "anonymous";
            var userAttrs = AnalysisEndpoints.ExtractUserAttributes(http);
            var answer = await rag.AnalyzeAsync(req, userId, userAttrs);
            return Results.Ok(answer);
        }).WithName("Analyze").Produces<AiAnswerDto>();
    }

    // FR-07 / IADR-0371 決定 2: 入力規則の判定。**規則そのものは `AnalyzeRequestValidator` が持つ。**
    //
    // 🔴 **`Errors[0]` を採る。** FluentValidation は既定で全規則を走らせるため、
    // 移送前の「最初の違反で 400 を返す」と同じ本文にするには最初の失敗を採るしかない。
    // 規則の宣言順が応答の契約の一部になっている（同 Validator のコメントを参照）。
    private static Result Validate(IValidator<AnalysisTaskRequest> validator, AnalysisTaskRequest req)
    {
        var result = validator.Validate(req);
        return result.IsValid
            ? Result.Success()
            : Result.Failure(Error.Validation("analysis.analyze.invalid", result.Errors[0].ErrorMessage));
    }
}
