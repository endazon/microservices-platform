using GraphService.Domain.Ports;
using Platform.Shared.Contracts.Dtos;
using System.Net.Http.Json;

namespace GraphService.Infrastructure.ExternalServices;

// FR-17, FR-05, UC-10, ADR-0004, ADR-0034: AuthorizationService の /authz/scope を呼び出し、
// 探索要求元の ABAC 許可スコープを解決する。
//
// **認可サービス障害時も deny-by-default（Granted=false）へ縮退し 500 を伝播させない**
// （WikiAccessResolver.ResolveAsync / RagOrchestrator.ResolveScopeAsync と同一方針）。
// グラフでは 1 文書の露出が近傍の存在まで明かすため、fail-open は特に許されない。
//
// **アクションは呼び出し側が明示する**（IADR-0272 決定 4。既定値は置かない）。読み取りは read、
// 書き込みは write —— **同じ 1 回の解決で「見えるか」と「書いてよいか」の両方には答えられない**
// （ADR-0034 決定 8 は閲覧権限を、ADR-0036 D-07 は書き込み権限を求めている）。
//
// **本サービスは 1 アクションにつきリクエストごとに 1 回だけ解決する。キャッシュは持たない。**
// ADR-0034 未決事項「ホップ展開結果のキャッシュ方針」は実装ガイド送りだが、決定 1 が
// 「キャッシュキーに利用者スコープを含める」ことのみ確定しており、ADR-0036 D-14 も
// 「キャッシュキーは必ず subject を含む —— 省くと他人の認可結果が漏れる」と定めている。
// 導入する場合はその制約に従うこと（本単位では導入しない）。
public class GraphAccessResolver(IHttpClientFactory httpFactory) : IGraphAccessResolver
{
    public async Task<AccessScopeResponse> ResolveAsync(
        HttpContext ctx, string action, CancellationToken ct = default)
    {
        var userId = ctx.User.Identity?.Name ?? "anonymous";
        var userAttrs = ExtractUserAttributes(ctx);

        var authzClient = httpFactory.CreateClient("AuthorizationService");
        try
        {
            var resp = await authzClient.PostAsJsonAsync("/authz/scope",
                new AccessScopeRequest(userId, userAttrs, action), ct);
            return (resp.IsSuccessStatusCode
                ? await resp.Content.ReadFromJsonAsync<AccessScopeResponse>(ct)
                : null) ?? new AccessScopeResponse(userId, [], false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // 通信失敗も deny-by-default へ縮退（権限外文書とその近傍の漏えい防止）。
            return new AccessScopeResponse(userId, [], false);
        }
    }

    // JWT クレームから ABAC 判定に用いる利用者属性を取り出す（WikiAccessResolver と同一）。
    //
    // 🔴 **読むのは clearance と department の 2 つだけである。** これはプラットフォーム全体の
    // 現状であり、本サービスが絞っているのではない。
    //
    // ［2026-08-28 追記 / #989 段 3］**「3 分岐 OR の表現構造が無い」は解消した。**
    // AccessScopeResponse は Branches を持ち（IADR-0253 決定 1）、AbacNodeFilter が分岐間 OR で
    // 評価する。**残っている制約は実データの側だけである** —— owner が 0% 充足であり（#516）、
    // owner ベースのポリシーも未配備なので、分岐が来ても現時点では見え方が変わらない。
    // **属性が付き owner ポリシーが入った時点で、追加改修なしに効く。**
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
