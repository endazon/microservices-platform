using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace ConversionService.Features.ConversionJobs.GetFigureImage;

// FR-12, UC-06, SC-07, [[IADR-0558]] (#1941): 縮退した図の元画像（人手補正の 2 ペインの右側）。BFF の
// `GET /bff/conversion/jobs/{id}/figures/{figureId}/image` が利用者の資格情報を付けて中継する（ADR-0109 決定 1。エッジ）。
//
// 問題詳細の `type` は BFF・画面・運用者が機械的に引く語であり、文言（`title` / `detail`）を変えても変えない。
internal static class GetFigureImageEndpoint
{
    public const string ImageUnavailableType = "urn:microservices-platform:problem:figure-image-unavailable";
    public const string ImageUnreachableType = "urn:microservices-platform:problem:figure-image-unreachable";

    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/{id:guid}/figures/{figureId}/image", async (Guid id, string figureId,
            FigureImageReader reader, CancellationToken ct) =>
        {
            FigureImageResult result;
            try
            {
                result = await reader.ReadAsync(id, figureId, ct);
            }
            catch (FigureImageStorageUnavailableException)
            {
                // 🔴 構成の不調を「画像が無い」（404）に化けさせない（#1905 / #1941 の再発防止）。
                return Results.Problem(
                    type: ImageUnreachableType,
                    title: "図の画像を取得できませんでした。",
                    detail: "変換サービスにオブジェクトストレージが構成されていません。",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return result.Status switch
            {
                FigureImageStatus.Available => Results.File(result.Bytes!, result.ContentType),
                FigureImageStatus.NotStored => Unavailable(
                    "図の画像の参照がオブジェクトストレージを指していません。", "not_stored"),
                FigureImageStatus.Missing => Unavailable(
                    "図の画像のオブジェクトがオブジェクトストレージにありません。", "missing"),
                // 未知のジョブ・未知の図・コード化済みの図（画像を持たない）は区別しない（IADR-0154 決定 2）。
                _ => Results.NotFound(),
            };
        }).WithName("ConversionJobFigureImage")
          // NFR-09, UC-06, ADR-0109 決定 3, IADR-0154 決定 6, IADR-0465: 2 ペインを開く操作そのものが人手補正であるため、
          // 照会（GET）だが管理者限定（BFF と同じ）。
          .RequireAuthorization(PlatformAuthPolicies.AdminOnly);
    }

    private static IResult Unavailable(string detail, string reason) => Results.Problem(
        type: ImageUnavailableType,
        title: "図の画像が保管されていません。",
        detail: detail,
        statusCode: StatusCodes.Status404NotFound,
        extensions: new Dictionary<string, object?> { ["reason"] = reason });
}
