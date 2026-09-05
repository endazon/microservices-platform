using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using System.Net.Http;
using System.Net.Http.Json;

namespace Knowledge.Bff.Endpoints;

// FR-17, SC-09, ADR-0033 決定 3・9, INDEX 決定 18 (#1241): 辺の型辞書（値集合）の管理。
// 後段は GraphService の `/graph/edge-types*` である。
//
// 🔴 **`/bff/graph/edge-types`（GraphBffEndpoints）とは別の口である。取り違えてはならない。**
//
//   | 口 | 後段 | 認可 | 使用件数 | 用途 |
//   | --- | --- | --- | --- | --- |
//   | `/bff/graph/edge-types` | `/graph/edge-types/catalog` | 認証のみ | **持たない** | SC-03 / SC-18 / SC-21 の描画・型フィルタ |
//   | `/bff/edge-types`（本モジュール） | `/graph/edge-types` | admin ＋ operator（書きは admin） | **持つ** | SC-09 の辞書管理 |
//
// **公開カタログを本辞書へ向け替えてはならない** —— (1) SC-03 / SC-18 / SC-21 を使う一般利用者が
// 403 になり (2) ホップごと ABAC が個々の辺を隠しているのに**集計値が総量を漏らす**。
// 逆に本辞書をカタログへ向けると SC-09 の「削除前に使用件数を示す」が満たせない。
// **だから prefix ごと分けてある**（`/bff/graph/*` の下に置くとルートも意味も衝突する）。
//
// 構成はタグ辞書（`TagDictionaryBffEndpoints`）と 1:1 で対応する ——
// INDEX 決定 18 が「同じ規則をタグ辞書にも適用する」と定めており、**規則が同じなら形も同じにする**。
public static class EdgeTypeDictionaryBffEndpoints
{
    public static IEndpointRouteBuilder MapEdgeTypeDictionaryBffEndpoints(this IEndpointRouteBuilder app)
    {
        // FR-17, SC-09 / SC-10: **読み取りは管理者・運用者**（後段 `EdgeTypeEndpoints` の read 群と同じ）。
        // SC-10 が型ごとの使用件数を出すため、運用者にも開く。
        var read = app.MapGroup("/bff/edge-types").WithTags("EdgeTypes BFF")
            .RequireAuthorization(p => p.RequireRole(
                PlatformAuthPolicies.AdminRole,
                PlatformAuthPolicies.OperatorRole));

        // FR-17, SC-09: **書き込みはシステム管理者限定**（SC-09 自体が platform-admin 限定の画面である）。
        //
        // **後段にも同じ AdminOnly がある**（[[IADR-0044]] の多層防御）。片側だけだと
        // 「BFF 迂回で通る」か「画面だけ 403 になる」のどちらかになる。
        var write = app.MapGroup("/bff/edge-types").WithTags("EdgeTypes BFF")
            .RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        read.MapGet("/", async (IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
        {
            var client = Forwarding(httpFactory, http);
            return await SendAsync(() => client.GetAsync("/graph/edge-types", ct), ct);
        }).WithName("BffEdgeTypeList").Produces<List<EdgeTypeDto>>();

        write.MapPost("/", async (CreateEdgeTypeRequest req, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
        {
            var client = Forwarding(httpFactory, http);
            return await SendAsync(() => client.PostAsJsonAsync("/graph/edge-types", req, ct), ct);
        }).WithName("BffEdgeTypeCreate").Produces<EdgeTypeDto>(StatusCodes.Status201Created);

        // ADR-0033 決定 9: **改名しても辺は 1 本も書き換わらない。** 辺は型 ID を参照しており、
        // 表示名は辞書で解決するので、追随は自動である。**BFF が追随の面倒を見る余地は無い。**
        write.MapPut("/{id:guid}", async (Guid id, RenameEdgeTypeRequest req, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
        {
            var client = Forwarding(httpFactory, http);
            return await SendAsync(() => client.PutAsJsonAsync($"/graph/edge-types/{id}", req, ct), ct);
        }).WithName("BffEdgeTypeRename").Produces<EdgeTypeDto>();

        // 🔴 **409 の本文を詰め替えない。** 後段は `{ error, message, usageCount }` を返しており、
        // SC-09 は「**削除前に使用件数を示す**」と定めている。`RelayAsync` が本文ごと透過するので
        // `usageCount` はそのまま画面へ届く。
        //
        // **BFF で数え直さない** —— 数え方を 2 つ持つと一覧と削除拒否で件数が割れ、
        // 管理者は辞書を信用できなくなる（タグ辞書がここで下したのと同じ判断）。
        write.MapDelete("/{id:guid}", async (Guid id, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
        {
            var client = Forwarding(httpFactory, http);
            return await SendAsync(() => client.DeleteAsync($"/graph/edge-types/{id}", ct), ct);
        }).WithName("BffEdgeTypeDelete");

        return app;
    }

    // GraphService は自分で ABAC を解決するので、**利用者の資格情報をそのまま転送する**
    // （GraphBffEndpoints と同じ「方式 A」。転送を忘れると後段は黙って 401/404 を返す）。
    private static HttpClient Forwarding(IHttpClientFactory httpFactory, HttpContext http)
    {
        var client = httpFactory.CreateClient("GraphService");
        var auth = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);
        return client;
    }

    // 到達できないときだけ 502 へ倒し、**後段が返した応答はそのまま透過する**。
    private static async Task<IResult> SendAsync(
        Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        try
        {
            var resp = await send();
            return await RelayAsync(resp, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    // **型付きで読み直して詰め替えない** —— それをすると後段の本文（409 の `usageCount` を含む）が失われる。
    private static async Task<IResult> RelayAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent)
            return Results.NoContent();
        var content = await resp.Content.ReadAsStringAsync(ct);
        var contentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
        return Results.Content(content, contentType, statusCode: (int)resp.StatusCode);
    }
}
