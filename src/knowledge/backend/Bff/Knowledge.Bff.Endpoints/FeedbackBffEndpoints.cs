using Knowledge.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Net.Http;
using System.Net.Http.Json;

namespace Knowledge.Bff.Endpoints;

// FR-08, UC-01: BFF フィードバック集約エンドポイント。
// チャット画面からの 👍/👎・コメント送信と満足率取得を FeedbackService へ委譲する。
//
// **［2026-08-10 / #521］認可を計画へ揃えた。** 従前この群には認可が無く、**無認証で投稿も
// 統計取得もできた**（他の BFF 群はすべて認可を持つ中で、ここだけが空白だった）。
// 計画 FR-08 は 2026-08-07 に **「投稿には認証を要する（匿名投稿は許さない）。統計は
// 運用者・管理者に限って参照できる」** を確定している（利用者裁定〔裁定依頼 planning#236・案 2〕。
// `02_requirements:33` / `05_screens:188` / `05_screens:431`）。
//
// **実効境界はサーバ側の多層防御である**（[[IADR-0044]]）——本 BFF と後段の FeedbackService の
// **両方**で塞ぐ。BFF だけに付けると、クラスタ内から後段へ直接到達する経路が空いたままになる。
public static class FeedbackBffEndpoints
{
    public static IEndpointRouteBuilder MapFeedbackBffEndpoints(this IEndpointRouteBuilder app)
    {
        // FR-08（計画確定 2026-08-07）: **投稿は認証必須・匿名は許さない。**
        // **ロールは要求しない**——計画は「認証を要する」までしか定めておらず、
        // **書かれていない制限を足すと一般利用者がフィードバックを送れなくなる**（FR-08 が成り立たない）。
        var g = app.MapGroup("/bff/feedback")
            .WithTags("Feedback BFF")
            .RequireAuthorization();

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
        }).WithName("BffFeedbackStats")
          // FR-08, SC-10（計画確定 2026-08-07・`05_screens:431`）: **統計は運用者・管理者に限る。**
          // SC-10 の画面の閲覧ロール（#544 で運用者へ開いた）と同じ線で引く。
          // 群の `RequireAuthorization()` と **AND 合成**され、実効は admin ＋ operator になる
          // （[[IADR-0128]] 決定 1）。
          .RequireAuthorization(p => p.RequireRole(
              PlatformAuthPolicies.AdminRole, PlatformAuthPolicies.OperatorRole))
          .Produces<FeedbackStatsDto>();

        return app;
    }
}
