using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Net.Http;
using System.Net.Http.Json;

namespace Knowledge.Bff.Endpoints;

// FR-08, UC-01: BFF フィードバック集約エンドポイント。
// チャット画面からの 👍/👎・コメント送信と満足率取得を FeedbackService へ委譲する。
public static class FeedbackBffEndpoints
{
    public static IEndpointRouteBuilder MapFeedbackBffEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/bff/feedback").WithTags("Feedback BFF");

        // FR-08, UC-01: 回答へのフィードバック送信を FeedbackService へ集約する。
        g.MapPost("/", async (
            FeedbackRequest req,
            IHttpClientFactory httpFactory,
            HttpContext http,
            CancellationToken ct) =>
        {
            var client = httpFactory.CreateClient("FeedbackService");

            // FR-08: 送信者を後段で特定できるよう Authorization ヘッダを伝播する。
            var auth = http.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrEmpty(auth))
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);

            var resp = await client.PostAsJsonAsync("/feedback", req, ct);
            if (!resp.IsSuccessStatusCode)
                return Results.StatusCode((int)resp.StatusCode);

            var dto = await resp.Content.ReadFromJsonAsync<FeedbackDto>(ct);
            // 新規(201)/更新(200) の区別を保つため、後段のステータスをそのまま透過する。
            return dto is null
                ? Results.StatusCode(StatusCodes.Status502BadGateway)
                : Results.Json(dto, statusCode: (int)resp.StatusCode);
        }).WithName("BffSubmitFeedback").Produces<FeedbackDto>();

        // FR-08: 満足率の集計取得（品質可視化）。
        g.MapGet("/stats", async (
            Guid? answerId,
            IHttpClientFactory httpFactory,
            CancellationToken ct) =>
        {
            var client = httpFactory.CreateClient("FeedbackService");
            var path = answerId is { } aid && aid != Guid.Empty
                ? $"/feedback/stats?answerId={aid}"
                : "/feedback/stats";

            var resp = await client.GetAsync(path, ct);
            if (!resp.IsSuccessStatusCode)
                return Results.StatusCode((int)resp.StatusCode);

            var stats = await resp.Content.ReadFromJsonAsync<FeedbackStatsDto>(ct);
            return stats is null
                ? Results.StatusCode(StatusCodes.Status502BadGateway)
                : Results.Ok(stats);
        }).WithName("BffFeedbackStats").Produces<FeedbackStatsDto>();

        return app;
    }
}
