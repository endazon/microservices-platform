using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using System.Net.Http;
using System.Net.Http.Json;

namespace Knowledge.Bff.Endpoints;

// FR-12, UC-06, SC-07, IADR-0042: 変換ジョブ管理の BFF 集約。
// ConversionService（/jobs）へプロキシする。運用系のため管理者・運用者ロールに限定する
// （IADR-0042 §決定3。画面側は RequireRole で存在秘匿）。利用者の資格情報は後段へ伝播する。
// **再変換（retry）だけは管理者ロールのみ**（05_screens §SC-07 2026-08-04 確定 / IADR-0128 決定1）。
// 照会（GET）は据え置く＝閲覧ロールは計画で裁定中のため巻き添えで変えない（IADR-0128 決定2）。
public static class ConversionBffEndpoints
{
    public static IEndpointRouteBuilder MapConversionBffEndpoints(this IEndpointRouteBuilder app)
    {
        // 照会・再変換に共通の下限。再変換はさらに下（MapPost）で管理者ロールへ絞る。
        var g = app.MapGroup("/bff/conversion/jobs")
            .WithTags("Conversion BFF")
            .RequireAuthorization(p => p.RequireRole(
                PlatformAuthPolicies.AdminRole,
                PlatformAuthPolicies.OperatorRole));

        // 一覧（?status=failed 等で絞り込み）。
        // SC-07 は変換状況の可視化が目的の運用画面のため、後段障害を空一覧へ縮退させない（「ジョブ無し」と
        // 「サービス障害」を誤認させない）。非 2xx はそのまま伝播し、後段不達は 502 で可視化する。
        // 個別取得・再変換と挙動を揃える（レビュー #172 指摘対応）。
        g.MapGet("/", async (IHttpClientFactory httpFactory, HttpContext http, string? status, CancellationToken ct) =>
        {
            var client = Forwarding(httpFactory, http);
            var path = string.IsNullOrWhiteSpace(status) ? "/jobs" : $"/jobs?status={Uri.EscapeDataString(status)}";
            try
            {
                var resp = await client.GetAsync(path, ct);
                if (!resp.IsSuccessStatusCode)
                    return Results.StatusCode((int)resp.StatusCode);
                var jobs = await resp.Content.ReadFromJsonAsync<List<ConversionJobDto>>(ct);
                return Results.Ok(jobs ?? []);
            }
            catch (Exception ex) when (IsTransient(ex, ct))
            {
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
        }).WithName("BffConversionJobList").Produces<List<ConversionJobDto>>();

        // 個別取得。
        g.MapGet("/{id:guid}", async (Guid id, IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
        {
            var client = Forwarding(httpFactory, http);
            var resp = await client.GetAsync($"/jobs/{id}", ct);
            if (!resp.IsSuccessStatusCode)
                return Results.StatusCode((int)resp.StatusCode);
            var job = await resp.Content.ReadFromJsonAsync<ConversionJobDto>(ct);
            return job is null ? Results.NotFound() : Results.Ok(job);
        }).WithName("BffConversionJobGet").Produces<ConversionJobDto>();

        // 人手補正（再変換）。後段は 202 / 404 / 409（失敗以外は再変換不可）を返す。そのまま中継する。
        // FR-12, SC-07（2026-08-04 確定）: 再変換の実行権限は管理者ロールに限る。画面のアクセス制御と
        // API の権限を揃える（API 側だけ緩いと画面の制御が意味を持たないため）。
        // 認可メタデータは AND 合成されるため、実効要件はグループの「admin または operator」と
        // AdminOnly の積＝**admin のみ**になる。グループを絞らないのは、閲覧ロールの裁定が計画側で
        // 継続中で、照会（GET）を巻き添えで変えないためである（IADR-0128 決定1・決定2）。
        g.MapPost("/{id:guid}/retry", async (Guid id, IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
        {
            var client = Forwarding(httpFactory, http);
            var resp = await client.PostAsync($"/jobs/{id}/retry", content: null, ct);
            return Results.StatusCode((int)resp.StatusCode);
        }).WithName("BffConversionJobRetry")
          .RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        return app;
    }

    private static HttpClient Forwarding(IHttpClientFactory httpFactory, HttpContext http)
    {
        var client = httpFactory.CreateClient("ConversionService");
        var auth = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);
        return client;
    }

    private static bool IsTransient(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested;
}
