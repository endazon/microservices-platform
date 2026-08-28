using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Net.Http;
using System.Net.Http.Json;

namespace Knowledge.Bff.Endpoints;

// FR-17, UC-10, ADR-0034, ADR-0043: グラフ読み取りを BFF から公開する（#916a）。
//
// 🔴 **権限伝播は「Authorization ヘッダの伝播」を採る。**
//
// 本リポジトリの BFF には権限伝播が 2 方式ある。
//   A) 利用者の JWT を後段へ伝播する（AnalysisBffEndpoints / AuthzBffEndpoints）
//   B) BFF が解決した AccessScope を本文へ載せる（SearchBffEndpoints → RetrievalService）
//
// **判断の軸は「後段が自分で ABAC を解決する型かどうか」である。**
// GraphService は自分で JWT から解決する（GraphAccessResolver。#908）ため A を採る。
//
// 🔴 **B を採ってはならない。** 本文で渡された scope を GraphService が信頼する形にすると、
// **その経路へ到達できる誰もが任意の scope を主張できる** —— ホップごと ABAC の型ゲート
// （IADR-0242 決定 2）が入力の時点で無意味になる。「RetrievalService が B だから揃える」は
// 理由にならない。あちらの設計判断を、自分で解決する型の後段へ横展開しない。
//
// ⚠️ **ヘッダを伝播し忘れると「全部 404」で静かに壊れる。** GraphService は利用者を
// anonymous として解決し Granted=false へ縮退するためである。動くように見える壊れ方では
// ないが「グラフには何も無い」と読めるので、陽性対照つきのテストで固定する（#916a 仕様書）。
//
// ── FR-18, SC-03 (#450): AI 提案の**承認・却下**を開けた（[[IADR-0300]]）
//
// 🔴 **前段の write ゲート（`PrivateNoteBffEndpoints.ForwardIfWritableAsync` 相当）は置かない。**
// あちらが置くのは**後段（DocumentService）が write スコープを見ないから**であり、
// GraphService は自分で解決する（`IsSourceWritableAsync`。#993 / [[IADR-0272]] 決定 2・3）。
// BFF に置ける門は `BffScopeResolver` の `Granted` だけ（**提案 ID から起点文書を引けないので
// 文書条件は当てられない**）で、後段の門が既にそれを包含する。足すと得るものが無いまま
//   (1) 拒否が **403** になり、後段が 404 へ倒している存在秘匿と応答が割れる（ADR-0034 決定 2）
//   (2) ABAC の判断点が 2 つになり、片方が腐っても気付けない（`DocumentBffEndpoints` が
//       所有者判定を持ち込まないと決めたのと同じ理由）
//   (3) 承認 1 回ごとに `/authz/scope` の往復が 1 つ増える
// の 3 つだけが増える。`DashboardBffEndpoints` の多層防御（[[IADR-0044]]）と割れて見えるが、
// **あちらが両側に置いたのは静的な「ロール」であって、要求ごとに解決する ABAC ではない。**
// ⚠️ **後段が write スコープを見ない口を本群へ足すときは、この判断が反転する**（そのときは足すこと）。
public static class GraphBffEndpoints
{
    public static IEndpointRouteBuilder MapGraphBffEndpoints(this IEndpointRouteBuilder app)
    {
        // NFR-09: 認証のみ（ロール不問）。可視性は GraphService の ABAC が決める。
        var g = app.MapGroup("/bff/graph").WithTags("Graph BFF").RequireAuthorization();

        // FR-17, UC-10: 起点ノード 1 件。
        g.MapGet("/{documentId:guid}", (Guid documentId, IHttpClientFactory httpFactory,
                HttpContext http, CancellationToken ct)
            => ProxyAsync<GraphViewDto>(httpFactory, http, $"/graph/{documentId}", ct))
            .WithName("BffGraphNode")
            .Produces<GraphViewDto>()
            .Produces(StatusCodes.Status404NotFound);

        // FR-17, UC-10, ADR-0034 決定 3: 近傍探索。
        // **hops はそのまま後段へ渡す。** 上限超過の拒否（400）は GraphService が一箇所で行い、
        // BFF では正規化も握り潰しもしない（検索モードを後段へ透過する SearchBff と同じ作法）。
        g.MapGet("/{documentId:guid}/neighbors", (Guid documentId, int? hops, string? by, string? types,
                IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct)
            => ProxyAsync<GraphViewDto>(httpFactory, http,
                $"/graph/{documentId}/neighbors" + BuildQuery(hops, by, types), ct))
            .WithName("BffGraphNeighbors")
            .Produces<GraphViewDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        // FR-17, SC-18 (#962): 辺の型カタログ。**辺の型名を解決するのに要る** ——
        // グラフ応答が返すのは `EdgeTypeId` だけなので、これが無いと辺の描き分けも型フィルタも描けない。
        //
        // 🔴 **後段は `/graph/edge-types/catalog`（認証のみ・使用件数なし）であって、
        // `/graph/edge-types`（admin / operator 限定・使用件数つき）ではない。**
        // 後者へ向けると (1) 一般利用者が 403 になり (2) ABAC で絞られていない集計値が漏れる。
        //
        // ルートの衝突は起きない —— 上の 2 本は `{documentId:guid}` に制約されており、
        // `edge-types` は GUID として解釈されない。
        g.MapGet("/edge-types", (IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct)
            => ProxyAsync<List<EdgeTypeCatalogItemDto>>(httpFactory, http, "/graph/edge-types/catalog", ct))
            .WithName("BffGraphEdgeTypes")
            .Produces<List<EdgeTypeCatalogItemDto>>()
            .Produces(StatusCodes.Status403Forbidden);

        // FR-18, UC-10, SC-21 (#918): **AI 提案の一覧**（読み取り）。
        //
        // 🔴 **［2026-08-29 追記 / #450］承認・却下は下に開けた。生成だけが閉じたままである。**
        // 従前ここには「開けるのは読み取り口だけである」と書いてあった —— 予告どおり
        // **承認欄（SC-03）と同じ変更単位で** 2 本を開けたので、その記述は失効した（[[IADR-0300]] 決定 1）。
        //   - 生成（`generate/{documentId}`）は**引き続き開けない**。計画（05_screens）は SC-03 にも
        //     SC-21 にも生成の導線を置いておらず、**消費者の無い書き込み口を先に公開面へ出さない**
        //     という理由がそのまま残る（#952 → #962 の教訓）。加えて後段自身が
        //     「正しいアクションは `analyze` である可能性が高く裁定待ち」と注記している。
        //   - 🔴 **一括承認の口はどの層にも作らない**（FR-18・SC-21「描いてはいけないもの」）。
        //     不在は `BffGraphSuggestionTests` がルート表の走査で固定する。**単票の承認・却下が
        //     在ることは、その走査の陽性対照になっている。**
        //
        // 🔴 **後段のパスは末尾スラッシュつきの `/graph/suggestions/` である**（群 `/graph/suggestions`
        // の直下に `MapGet("/")` で生えている）。スラッシュを落とすと 404 になり、
        // 画面には「提案が 1 件も無い」ではなく後段エラーとして出る。
        //
        // ルートの衝突は起きない —— `{documentId:guid}` の 2 本は GUID に制約されており、
        // `suggestions` は GUID として解釈されない（`edge-types` と同じ）。
        g.MapGet("/suggestions", (string? state, string? kind,
                IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct)
            => ProxyAsync<List<AiSuggestionDto>>(httpFactory, http,
                "/graph/suggestions/" + BuildSuggestionQuery(state, kind), ct))
            .WithName("BffGraphSuggestions")
            .Produces<List<AiSuggestionDto>>()
            .Produces(StatusCodes.Status400BadRequest);

        // FR-18, UC-10, SC-03, ADR-0033 決定 7 (#450): **承認。1 件ずつ。**
        //
        // 🔴 **後段の状態コードをそのまま返す。** 承認は「権限外・不存在・write 権限なし」を
        // すべて 404 に倒しており（ADR-0034 決定 2 / [[IADR-0272]] 決定 3）、ここで 403 や 200 へ
        // 変換すると**存在秘匿が BFF 層で破れる**。409（`invalid_transition`）・
        // 400（`unknown_edge_type`）も**本文ごと**透過する —— 画面は理由で文言を出し分ける。
        //
        // 🔴 **後段パスは `/graph/suggestions/{id}/approve`**（群 `/graph/suggestions` の直下）。
        // 一覧と違い**末尾スラッシュは付かない**（`MapPost("/{id:guid}/approve")` である）。
        g.MapPost("/suggestions/{id:guid}/approve", (Guid id, IHttpClientFactory httpFactory,
                HttpContext http, CancellationToken ct)
            => ForwardAsync(HttpMethod.Post, $"/graph/suggestions/{id}/approve", httpFactory, http, ct))
            .WithName("BffGraphSuggestionApprove")
            .Produces<AiSuggestionDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        // FR-18, UC-10, SC-03, ADR-0033 決定 7・10 (#450): **却下。1 件ずつ。**
        //
        // 🔴 **本文を送らない。** 後段の `RejectAiSuggestionRequest`（両端の本文指紋）は任意であり、
        // **SPA は指紋を持てない** —— `AiSuggestionDto` は指紋を公開面へ出さないと決めてある
        // （[[IADR-0276]] 決定 2。出すと文書を読めない利用者に本文の変化を判定させる副次経路になる）。
        // 帰結は [[IADR-0300]] 決定 3 に実測つきで書いた（**解除の発火条件は変わらない**）。
        g.MapPost("/suggestions/{id:guid}/reject", (Guid id, IHttpClientFactory httpFactory,
                HttpContext http, CancellationToken ct)
            => ForwardAsync(HttpMethod.Post, $"/graph/suggestions/{id}/reject", httpFactory, http, ct))
            .WithName("BffGraphSuggestionReject")
            .Produces<AiSuggestionDto>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    // FR-18, SC-21 (#918): 状態・種類の絞りを**そのまま後段へ渡す**。
    //
    // **正規化も既定値の補完も検証も BFF では行わない**（グラフ読み取りと同じ作法）。
    // 既定（未指定 = `pending`）と値域の検査（`invalid_state` / `invalid_kind` の 400）は
    // GraphService が一箇所で持つ。ここで既定を補うと、**既定値の情報源が 2 つ**になる。
    private static string BuildSuggestionQuery(string? state, string? kind)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(state)) parts.Add($"state={Uri.EscapeDataString(state)}");
        if (!string.IsNullOrWhiteSpace(kind)) parts.Add($"kind={Uri.EscapeDataString(kind)}");
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }

    // FR-17, SC-18, ADR-0049 (#980), #917: hops・間引き基準・辺の型フィルタを**そのまま後段へ渡す**。
    // 正規化も既定値の補完も検証も BFF では行わない（一箇所で決める。GraphService が持つ ——
    // types の形式不正 400 も GraphService が返し、BFF はそのまま透過する）。
    private static string BuildQuery(int? hops, string? by, string? types = null)
    {
        var parts = new List<string>();
        if (hops is not null) parts.Add($"hops={hops}");
        if (!string.IsNullOrWhiteSpace(by)) parts.Add($"by={Uri.EscapeDataString(by)}");
        if (!string.IsNullOrWhiteSpace(types)) parts.Add($"types={Uri.EscapeDataString(types)}");
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }

    // FR-18, SC-03 (#450): 書き込み（POST）を GraphService へ中継する。
    //
    // **`ProxyAsync<T>` を使い回せない** —— あちらは `client.GetAsync` を直に呼ぶ GET 専用であり、
    // かつ本文を型付きで読み直して `Results.Ok` へ詰め替える（＝**後段の本文が失われる**）。
    // 承認・却下は 409 / 400 の**本文**（`{ error, state }`）が画面の文言の根拠なので、
    // `PrivateNoteBffEndpoints.ForwardAsync` / `RelayAsync` と同じく**本文ごと透過する**。
    //
    // 🔴 資格情報の伝播は読み取りと同じ方式 A である（冒頭の注釈）。落とすと後段は利用者を
    // anonymous として解決し、**承認が静かに全件 404 になる**（陽性対照つきのテストで固定する）。
    private static async Task<IResult> ForwardAsync(
        HttpMethod method, string path,
        IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct)
    {
        var client = httpFactory.CreateClient("GraphService");
        var auth = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);

        using var req = new HttpRequestMessage(method, path);
        try
        {
            var resp = await client.SendAsync(req, ct);
            return await RelayAsync(resp, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // 後段へ到達できない。**成功へ縮退しない** —— 承認できていないのに承認済みと
            // 見えるのが最悪である（辺が生まれたと誤認したまま棚卸しが進む）。
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    // 後段の応答（status・content-type・本文）をそのまま返す。
    //
    // 🔴 **409（`invalid_transition`）・400（`unknown_edge_type`）・404（存在秘匿）を保つために必須**
    // である。詰め替えると画面は「もう承認済み」と「辺の型が消えている」を区別できない。
    private static async Task<IResult> RelayAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent)
            return Results.NoContent();
        var content = await resp.Content.ReadAsStringAsync(ct);
        var contentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
        return Results.Content(content, contentType, statusCode: (int)resp.StatusCode);
    }

    // GraphService へ中継する。
    //
    // 🔴 **状態コードはそのまま返す。** とくに 404 を別の値へ置き換えない ——
    // GraphService は「権限外・複製未到達・不存在」をすべて 404 に倒して存在を秘匿しており
    // （ADR-0034 決定 2）、BFF がここで 403 や 200 へ変換すると**その秘匿が BFF 層で破れる**。
    private static async Task<IResult> ProxyAsync<T>(
        IHttpClientFactory httpFactory, HttpContext http, string path, CancellationToken ct)
    {
        var client = httpFactory.CreateClient("GraphService");

        // FR-05: ABAC 権限解決のため、利用者の資格情報を後段へ引き継ぐ（権限外文書を出さない）。
        var auth = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);

        try
        {
            var resp = await client.GetAsync(path, ct);
            if (!resp.IsSuccessStatusCode)
                return Results.StatusCode((int)resp.StatusCode);

            var view = await resp.Content.ReadFromJsonAsync<T>(ct);
            return view is null
                ? Results.StatusCode(StatusCodes.Status502BadGateway)
                : Results.Ok(view);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // 後段へ到達できない。**空応答へ縮退しない** —— グラフが「空」と「引けない」は
            // 利用者にとって別の意味であり、空を返すと「関係が無い」と読めてしまう。
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }
}
