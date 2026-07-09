using KnowledgePlatform.Shared.Contracts.Dtos;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Extensions;
using System.Net.Http.Json;

namespace KnowledgePlatform.Bff.Foundation.Endpoints;

// FR-12, UC-06, SC-07, IADR-0042: 変換ジョブ管理の BFF 集約。
// ConversionService（/jobs）へプロキシする。運用系のため管理者・運用者ロールに限定する
// （IADR-0042 §決定3。画面側は RequireRole で存在秘匿）。利用者の資格情報は後段へ伝播する。
public static class ConversionBffEndpoints
{
    public static IEndpointRouteBuilder MapConversionBffEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/bff/conversion/jobs")
            .WithTags("Conversion BFF")
            .RequireAuthorization(p => p.RequireRole(
                KnowledgePlatformAuthPolicies.AdminRole,
                KnowledgePlatformAuthPolicies.OperatorRole));

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

        // 人手補正（再変換）。後段は 202 / 404 を返す。そのまま中継する。
        g.MapPost("/{id:guid}/retry", async (Guid id, IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
        {
            var client = Forwarding(httpFactory, http);
            var resp = await client.PostAsync($"/jobs/{id}/retry", content: null, ct);
            return Results.StatusCode((int)resp.StatusCode);
        }).WithName("BffConversionJobRetry");

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
