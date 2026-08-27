using Microsoft.AspNetCore.Http;
using Platform.Shared.Contracts.Dtos;
using System.Net.Http.Json;

namespace Platform.Shared.Infrastructure.Foundation.Authz;

// FR-05, UC-01, IADR-0009: BFF 集約点での ABAC スコープ解決（横断検索・文書閲覧の共通前段）。
// JWT からサーバ側で利用者を特定し、AuthorizationService でスコープを解決する。クライアントが
// 指定した Scope は信頼しない（権限昇格の防止）。deny-by-default: 許可ポリシーが無い／解決不能な
// 場合は null（＝閲覧可能なし）へ縮退する。呼び出し側は null を「空応答」または「404 秘匿」へ写す。
public static class BffScopeResolver
{
    // 利用者の許可スコープを解決する。許可（Granted=true）のときのみ AccessScope を返し、
    // それ以外（未マッチ・認可サービス不調）は null を返す。
    //
    // FR-05, FR-06, ADR-0036 D-07, IADR-0272 決定 4 (#1010): **action は既定値の無い必須引数である。**
    // #1010 は「/authz/scope が read を返すこと」を暗黙の前提にした呼び出しが BFF の書き込み経路へ
    // 効いていた欠陥（#993 の platform 共通版）。既定値を残すと、新しい経路を足した人が書き忘れる
    // ことで認可が緩む。既定値を外せばアクションの選択がコンパイラに強制される。
    // 読み取りは BffScopeAction.Read、作成・更新・削除は BffScopeAction.Write を渡す。
    public static async Task<AccessScope?> ResolveAsync(
        IHttpClientFactory httpFactory, HttpContext http, string action, CancellationToken ct)
    {
        var userId = http.User.Identity?.Name ?? "anonymous";
        var userAttrs = ExtractUserAttributes(http);

        var authzClient = httpFactory.CreateClient("AuthorizationService");
        try
        {
            var scopeResp = await authzClient.PostAsJsonAsync("/authz/scope",
                new AccessScopeRequest(userId, userAttrs, action), ct);
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

// FR-05, FR-21, ADR-0036 D-07, IADR-0253 決定 5, IADR-0272 決定 4 (#1010):
// BFF が解決するアクションの語彙。
//
// **値域の正本は AuthorizationService の `PolicyAction`**（read / analyze / manage / write）だが、
// 共有基盤（Platform.Shared.Infrastructure）からサービスプロジェクトは参照できず、契約 DTO
// （Platform.Shared.Contracts）は値域を持たない（既定値のリテラル "read" だけを持つ）。
// したがってここに写しを置く（GraphService の GraphAccessAction と同じ形・同じ理由）。
//
// **綴りがずれても緩む向きには壊れない** —— /authz/scope は値域外を 400 で返し、
// BffScopeResolver は非 2xx を null（deny-by-default）へ縮退させる。
public static class BffScopeAction
{
    // 閲覧・検索・属性値照会（存在秘匿つきの読み取り経路）。
    public const string Read = "read";

    // 作成・更新・削除（ADR-0036 D-07: doc.owner ∈ { ${current_user} }）。
    public const string Write = "write";
}
