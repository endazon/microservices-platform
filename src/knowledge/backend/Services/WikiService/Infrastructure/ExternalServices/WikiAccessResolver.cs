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
    // UC-07 事前条件「**認証済み**」（#1126 / IADR-0335）。**未認証は認可サービスを呼ばずに拒否する。**
    // 匿名でも到達し得る要求へ与える身元。**認可サービスへは渡らない**（この値で問い合わせない）。
    private const string AnonymousUserId = "anonymous";

    public async Task<AccessScopeResponse> ResolveAsync(HttpContext ctx, CancellationToken ct = default)
    {
        // 🔴 UC-07 事前条件, FR-05, IADR-0044（多層防御）, #1126:
        // **未認証の要求は、認可サービスへ問い合わせずに deny-by-default で返す。**
        //
        // 従前は未認証でも `anonymous` を身元として `/authz/scope` を叩いていた。fail-closed に
        // *見えていた*だけで、**利用者条件を持たないポリシーが 1 件でも入れば匿名にも許可が下りる**
        // ——「未認証時の応答」がポリシーの内容次第で変わる、固定されていない契約だった（#1126）。
        // ここで短絡させることで、Wiki 前段の 4 経路の匿名応答が**ポリシーに依らず**
        // 一覧・検索 = 200 ＋ 空、個別 = 404（存在秘匿・IADR-0009）に定まる。
        //
        // **401 にはしない。** エッジは BFF（ADR-0032 / Token Handler）であり、ここは mesh 内の
        // 後段である。既存 3 経路は空／404 を返す契約で固定されており、状態コードを変えると
        // 4 経路のうち 3 本の契約が黙って変わる（判断の記録は IADR-0335）。
        if (ctx.User.Identity?.IsAuthenticated != true)
            return new AccessScopeResponse(AnonymousUserId, [], false);

        var userId = ctx.User.Identity.Name ?? AnonymousUserId;
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
