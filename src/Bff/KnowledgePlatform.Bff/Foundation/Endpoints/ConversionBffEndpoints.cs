using KnowledgePlatform.Shared.Contracts.Dtos;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Extensions;
using System.Net.Http.Json;

namespace KnowledgePlatform.Bff.Foundation.Endpoints;

// FR-12, UC-06, SC-07, IADR-0039/IADR-0042: 変換ジョブ管理の BFF 集約。
// ConversionService（/jobs）へプロキシする。運用系のため管理者・運用者ロールに限定する
// （画面側は RequireRole で存在秘匿）。利用者の資格情報は後段へ伝播する。
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
        g.MapGet("/", async (IHttpClientFactory httpFactory, HttpContext http, string? status, CancellationToken ct) =>
        {
            var client = Forwarding(httpFactory, http);
            var path = string.IsNullOrWhiteSpace(status) ? "/jobs" : $"/jobs?status={Uri.EscapeDataString(status)}";
            try
            {
                var jobs = await client.GetFromJsonAsync<List<ConversionJobDto>>(path, ct);
                return Results.Ok(jobs ?? []);
            }
            catch (Exception ex) when (IsTransient(ex, ct))
            {
                return Results.Ok(new List<ConversionJobDto>());
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
