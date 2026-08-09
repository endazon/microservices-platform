using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using System.Net.Http;
using System.Net.Http.Json;

namespace Knowledge.Bff.Endpoints;

// FR-09, SC-09, UC-05, #640: タグ辞書の管理（追加・改名・削除）を BFF へ通す。
//
// **#634 / #635 は口を DocumentService 側にだけ置いた。** 読み取りだけが
// `POST /bff/attribute-values` の `dictionary` へ相乗りしており（[[IADR-0152]] 決定 3）、
// **書き込み口が無かった**。フロントからは必ず `/bff/*` を通る規約（`CLAUDE.md`「BFF 境界」。
// `foundation/api` 以外での `fetch` は ESLint が error にする）なので、
// **SC-09 の画面は辞書を読めても 1 つも編集できなかった**。
//
// **読み取り口が BFF に無かったのは意図した設計ではない** —— ADR-0043 決定 4 が定めるのは
// 「**読み口は 1 系統**」であって、書き込み口を置かない理由ではない。
//
// **［判断］群は `/bff/tags` として knowledge 側に置く**（作業仕様書 §判断 1）。
// `/bff/admin/authz` は `Platform.Bff` の群で後段は AuthorizationService である。
// タグ辞書の後段は **DocumentService（knowledge ユニット）**なので、あちらへ寄せると
// **platform の群が可変ユニットの資源を配る**ことになり `CLAUDE.md` の依存規則に触れる。
// **画面が同じ SC-09 であることは口の所属を決めない**（[[IADR-0139]] の「判定の単位は
// ドメインではなく資源」と同じ切り分け）。
public static class TagDictionaryBffEndpoints
{
    public static IEndpointRouteBuilder MapTagDictionaryBffEndpoints(this IEndpointRouteBuilder app)
    {
        // FR-09, SC-05, SC-09: **読み取りは管理者・運用者**（裁定 Q18。[[IADR-0152]] 決定 5）。
        // 後段 `GET /tags` と同じロール要件である。
        var read = app.MapGroup("/bff/tags").WithTags("Tags BFF")
            .RequireAuthorization(p => p.RequireRole(
                PlatformAuthPolicies.AdminRole,
                PlatformAuthPolicies.OperatorRole));

        // FR-09, SC-09: **書き込みはシステム管理者限定**（SC-09 のアクセス制御）。
        //
        // **[[IADR-0044]] の多層防御**——後段 `TagDictionaryEndpoints` の `write` 群も
        // `AdminOnly` である。**片側だけだと「BFF 迂回で通る」か「画面だけ 403 になる」**の
        // どちらかが起きる。
        //
        // **機械クライアントは居ない**（作業仕様書 §母集合 軸 3 で実測）。`/tags` を叩く本番コードは
        // BFF 自身の読み取り 1 本だけで、`ai-stock-trading` からの呼び出しは 0 件である。
        // **#629 の `POST /documents` のような据え置きは要らない。**
        var write = app.MapGroup("/bff/tags").WithTags("Tags BFF")
            .RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        // FR-09, SC-09: 辞書の一覧（値集合 ＋ 使用件数）。
        //
        // **`/bff/attribute-values` の `dictionary` と同じ後段を引く**が、こちらは
        // **辞書そのものを目的とする口**である。あちらは検索の候補一覧に辞書を「添える」形で、
        // `key=tags` のときだけ付く（[[IADR-0152]] 決定 3）。**管理画面が候補一覧の口を
        // 経由して辞書を取るのは筋が悪い** —— 検索の都合で形が変わる。
        read.MapGet("/", async (IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
        {
            var client = Forwarding(httpFactory, http);
            var resp = await client.GetAsync("/tags", ct);
            return await RelayAsync(resp, ct);
        }).WithName("BffTagList").Produces<TagDictionaryResponse>();

        // FR-09, SC-09: 辞書へタグを追加する。**名前の重複は 409**（後段が判定する）。
        write.MapPost("/", async (CreateTagRequest req, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
        {
            var client = Forwarding(httpFactory, http);
            var resp = await client.PostAsJsonAsync("/tags", req, ct);
            return await RelayAsync(resp, ct);
        }).WithName("BffTagCreate").Produces<TagDto>(StatusCodes.Status201Created);

        // FR-09, SC-09: 改名する（SC-09「改名は既存文書へ追随する」）。
        //
        // **応答の `RepublishedDocuments` をそのまま画面へ通す** —— 射影（Qdrant / Wiki.js）の
        // 反映は非同期なので、**「0 件だった」と「まだ届いていない」を管理者が切り分けられる**
        // （[[IADR-0153]] 決定 3）。
        write.MapPut("/{id:guid}", async (Guid id, RenameTagRequest req, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
        {
            var client = Forwarding(httpFactory, http);
            var resp = await client.PutAsJsonAsync($"/tags/{id}", req, ct);
            return await RelayAsync(resp, ct);
        }).WithName("BffTagRename").Produces<RenameTagResponse>();

        // FR-09, SC-09: 削除する。**使用件数が 0 件のときだけ許す**（[[IADR-0153]] 決定 6）。
        //
        // **409 の本文を詰め替えない。** 後段は `{ error, message, usageCount }` を返しており、
        // SC-09 は「**削除前に使用件数を示す**」と定めている。`RelayAsync` が本文ごと透過するので
        // `usageCount` はそのまま画面へ届く（[[IADR-0040]] の透過中継と同型）。
        //
        // **BFF で数え直さない** —— 数え方を 2 つ持つと一覧と削除拒否で件数が割れ、
        // 管理者が辞書を信用できなくなる（後段 `LoadWithUsageAsync` と同じ母集合を保つ）。
        write.MapDelete("/{id:guid}", async (Guid id, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
        {
            var client = Forwarding(httpFactory, http);
            var resp = await client.DeleteAsync($"/tags/{id}", ct);
            return await RelayAsync(resp, ct);
        }).WithName("BffTagDelete");

        return app;
    }

    // FR-09: 利用者の資格情報を後段へ引き継ぐ DocumentService クライアントを生成する。
    // **後段が最終防衛線として同じロールを検査する**（[[IADR-0044]]）。
    private static HttpClient Forwarding(IHttpClientFactory httpFactory, HttpContext http)
    {
        var client = httpFactory.CreateClient("DocumentService");
        var auth = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);
        return client;
    }

    // 後段の応答を**本文ごと**透過する（状態コード・Content-Type も保つ）。
    // **409 の `usageCount` と 400 の検証内容を保つために必須である** ——
    // 詰め替えると画面が件数を出せない（SC-09 の確定規則を満たせなくなる）。
    private static async Task<IResult> RelayAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent)
            return Results.NoContent();
        var content = await resp.Content.ReadAsStringAsync(ct);
        var contentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
        return Results.Content(content, contentType, statusCode: (int)resp.StatusCode);
    }
}
