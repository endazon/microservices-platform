using KnowledgePlatform.Shared.Contracts.Dtos;
using System.Net.Http.Json;

namespace KnowledgePlatform.Bff.Foundation.Authz;

// FR-05, UC-01, IADR-0009: BFF 集約点での ABAC スコープ解決（横断検索・文書閲覧の共通前段）。
// JWT からサーバ側で利用者を特定し、AuthorizationService でスコープを解決する。クライアントが
// 指定した Scope は信頼しない（権限昇格の防止）。deny-by-default: 許可ポリシーが無い／解決不能な
// 場合は null（＝閲覧可能なし）へ縮退する。呼び出し側は null を「空応答」または「404 秘匿」へ写す。
public static class BffScopeResolver
{
    // 利用者の許可スコープを解決する。許可（Granted=true）のときのみ AccessScope を返し、
    // それ以外（未マッチ・認可サービス不調）は null を返す。
    public static async Task<AccessScope?> ResolveAsync(
        IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct)
    {
        var userId = http.User.Identity?.Name ?? "anonymous";
        var userAttrs = ExtractUserAttributes(http);

        var authzClient = httpFactory.CreateClient("AuthorizationService");
        try
        {
            var scopeResp = await authzClient.PostAsJsonAsync("/authz/scope",
                new AccessScopeRequest(userId, userAttrs), ct);
            var resolved = scopeResp.IsSuccessStatusCode
                ? await scopeResp.Content.ReadFromJsonAsync<AccessScopeResponse>(ct)
                : null;

            // deny-by-default: 許可ポリシーが無い/解決不能 → 閲覧可能なし。
            if (resolved is not { Granted: true })
                return null;

            return new AccessScope(resolved.AllowedFilters, resolved.Granted);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // 認可サービス不調は deny-by-default（null）へ縮退する。
            return null;
        }
    }

    // FR-05: JWT から ABAC 判定に用いる利用者属性を取り出す（検索・分析と同一の属性キー）。
    public static Dictionary<string, string> ExtractUserAttributes(HttpContext ctx)
    {
        var attrs = new Dictionary<string, string>();
        var clearance = ctx.User.FindFirst("clearance")?.Value;
        var department = ctx.User.FindFirst("department")?.Value;
        if (clearance is not null) attrs["clearance"] = clearance;
        if (department is not null) attrs["department"] = department;
        return attrs;
    }

    // FR-05: 単一文書の属性がスコープに合致するか判定する（AbacEvaluator と同一意味論）。
    // フィルタ間は AND、値集合内は OR。Filters が空かつ GrantsAccess=true は「条件なしで全件許可」。
    public static bool Matches(IReadOnlyDictionary<string, string> attributes, AccessScope scope)
    {
        if (!scope.GrantsAccess)
            return false;
        foreach (var filter in scope.Filters)
        {
            if (!attributes.TryGetValue(filter.Key, out var value))
                return false;
            if (!filter.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}
