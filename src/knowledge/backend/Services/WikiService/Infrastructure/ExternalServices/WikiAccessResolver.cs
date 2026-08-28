using WikiService.Domain.Ports;
using Platform.Shared.Contracts.Dtos;
using System.Net.Http.Json;

namespace WikiService.Infrastructure.ExternalServices;

// FR-13, FR-05, UC-07, ADR-0011, ADR-0004: AuthorizationService の /authz/scope を呼び出し、
// 閲覧要求元の ABAC 許可スコープを解決する。
// 認可サービス障害時も deny-by-default（Granted=false）へ縮退し 500 を伝播させない
// （RagOrchestrator.ResolveScopeAsync と同一方針）。
public class WikiAccessResolver(IHttpClientFactory httpFactory) : IWikiAccessResolver
{
    public async Task<AccessScopeResponse> ResolveAsync(HttpContext ctx, CancellationToken ct = default)
    {
        var userId = ctx.User.Identity?.Name ?? "anonymous";
        var userAttrs = ExtractUserAttributes(ctx);

        var authzClient = httpFactory.CreateClient("AuthorizationService");
        try
        {
            var resp = await authzClient.PostAsJsonAsync("/authz/scope",
                new AccessScopeRequest(userId, userAttrs), ct);
            return (resp.IsSuccessStatusCode
                ? await resp.Content.ReadFromJsonAsync<AccessScopeResponse>(ct)
                : null) ?? new AccessScopeResponse(userId, [], false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // 認可サービスへの通信失敗も deny-by-default へ縮退（権限外文書の漏えい防止）。
            return new AccessScopeResponse(userId, [], false);
        }
    }

    // JWT クレームから ABAC 判定に用いる利用者属性を取り出す（AnalysisEndpoints と同一）。
    private static Dictionary<string, string> ExtractUserAttributes(HttpContext ctx)
    {
        var attrs = new Dictionary<string, string>();
        var clearance = ctx.User.FindFirst("clearance")?.Value;
        var department = ctx.User.FindFirst("department")?.Value;
        if (clearance is not null) attrs["clearance"] = clearance;
        if (department is not null) attrs["department"] = department;
        return attrs;
    }
}
