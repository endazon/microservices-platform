using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Net.Http;

namespace Knowledge.Bff.Endpoints;

// FR-13, UC-07, SC-04, ADR-0011, ADR-0032, ADR-0073 決定 2・4, IADR-0009 / IADR-0020 / IADR-0335 /
// IADR-0355, #1199: Wiki 前段（WikiService の `/wiki/*`）を画面用の口へ露出する透過中継。
//
// **後段の 4 経路は先に入っており（IADR-0020 / IADR-0335）、本ファイルはその真ん中の 1 本である。**
// これが入るまで SPA から Wiki の内容へ到達する経路は 1 本も無かった。
//
// ── なぜ今まで作らなかったか（記録を消さずに引く）
// [[IADR-0335]] は「**`/bff/wiki/*` は作らない**。SC-04 は Wiki.js 別ホスト・基盤 SPA とは別配信で
// あり SPA は導線しか持たない。検索だけ露出面が違う状態を作らない。露出は SC-04 の実現方式が
// 決まってから 1 回で行う」とフォローアップに置いた。**ADR-0073 決定 2 がその実現方式を確定させ**
// （SC-04 は基盤 SPA のルートとし、BFF 経由で取得して SPA が描く）、**決定 4 が「IADR-0335 の判断は
// 正しかった。本決定がその『1 回でまとめて行う』時点である」**と明示した。よって 4 経路を同時に開く。
//
// ── 認可の姿（層ごとに分けて書く）
//
// 1. **認証は必須・ロールは要求しない**（契約の `x-roles: []`）。計画 `05_screens` は利用者グループ
//    （SC-01〜04）を「**ABAC の権限内で全利用者が利用できる**」と定めている。ロールを足すと
//    **一般利用者が Wiki を 1 ページも開けなくなる**。可視性を決めるのは役割ではなく ABAC である。
//
// 2. 🔴 **権限伝播は「`Authorization` ヘッダの伝播」（方式 A）を採る。**
//    本リポジトリの BFF には 2 方式ある —— A) 利用者の JWT を後段へ伝播する
//    （`GraphBffEndpoints` / `AnalysisBffEndpoints`）、B) BFF が解決した `AccessScope` を本文へ載せる
//    （`SearchBffEndpoints` → RetrievalService）。**判断の軸は「後段が自分で ABAC を解決する型か」**で
//    ある。WikiService は `IWikiAccessResolver`（`/authz/scope` を自分で叩く）で解決する型なので A。
//    **B を採ってはならない** —— 本文で渡された scope を後段が信じる形にすると、その経路へ到達できる
//    誰もが任意の scope を主張できる。
//
//    ⚠️ **伝播を落とすと「全部空・全部 404」で静かに壊れる。** `WikiAccessResolver` は未認証を
//    `Granted=false` へ短絡させる（[[IADR-0335]] 決定 4）ため、ヘッダが届かないと一覧・検索は
//    200 ＋ 空、個別は 404 になる —— **「Wiki に何も無い」と読める壊れ方**である。
//    陽性対照つきのテスト（`BffWikiEndpointTests`）で固定する。
//    BFF セッション方式（ADR-0032 / [[IADR-0251]] / [[IADR-0273]]）では
//    `SessionTokenPropagationMiddleware` がセッション Cookie のアクセストークンを `Authorization` へ
//    載せるので、ここはその結果を読むだけでよい（**新しい方式を発明しない**）。
//
// 3. 🔴 **BFF 側に ABAC の前段（`BffScopeResolver`）を置かない。** `GraphBffEndpoints` と同じ理由で
//    ある。置いても得るものが無く、次の 3 つだけが増える。
//      (1) 拒否が **403** になり、後段が 404 へ倒している存在秘匿と応答が割れる（[[IADR-0009]]）
//      (2) ABAC の判断点が 2 つになり、片方が腐っても気付けない
//      (3) 後段が必ず行う `/authz/scope` の往復が**二重になる**
//    **BFF に置ける門は `Granted` だけ**で、文書条件（`AbacPageFilter`）は台帳の行が要るため当てられない。
//    後段の門がそれを包含する。
//
// 4. 🔴 **応答は透過する。状態コードを作り替えない。** 後段は「権限外・不存在・アーカイブ済み」を
//    区別せず **404** を返し（存在秘匿。[[IADR-0009]] / ADR-0011）、一覧・検索は「権限が無い」と
//    「該当が無い」を区別せず **200 ＋ 空**を返す。403 や別の 200 へ変換すると**秘匿が BFF 層で破れる**。
//    Wiki.js 不達の **502**（[[IADR-0335]] 決定 2。故障は空で隠さない）もそのまま通す。
//
// 5. **BFF から後段へ到達できない場合も 502。** 空の 200 へ縮退すると「Wiki に何も無い」と読ませる
//    —— 権限で消えたのか壊れているのかを利用者が区別できなくなる（4 の後段側と同じ切り分け）。
//
// 6. **クエリの既定・上限を BFF が持たない。** `q` / `limit` は**指定されたときだけ**後段へ載せる。
//    既定 20 / 上限 50 のクランプは後段 `SearchWikiPagesEndpoint` が唯一の情報源である
//    （2 つ持つと、後段を変えたとき BFF だけ古い上限で切る。[[IADR-0346]] 決定 4 と同じ）。
//
// ── 決定の記録: 上記 1〜6 と置き場所（knowledge 側）の理由は [[IADR-0355]] が正本である。
public static class WikiBffEndpoints
{
    /// <summary>後段（WikiService）の named HttpClient 名。Program.cs の登録と一致させる。</summary>
    public const string ClientName = "WikiService";

    public static IEndpointRouteBuilder MapWikiBffEndpoints(this IEndpointRouteBuilder app)
    {
        // 上の 1: 認証だけを要求する（`RequireAuthorization()` にロール要件を足さない）。
        var g = app.MapGroup("/bff/wiki").WithTags("Wiki BFF").RequireAuthorization();

        // FR-13, UC-07 基本フロー 1「開く」: 権限内ページの一覧（本文は含まない）。
        // 後段は許可が無ければ **200 ＋ 空**を返す（deny-by-default）。それをそのまま返す。
        g.MapGet("/pages", (IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            ProxyAsync(f, h, "/wiki/pages", ct))
            .WithName("BffWikiPageList");

        // FR-13, UC-07 基本フロー 1「検索する」: 全文検索は Wiki.js へ委譲し、前段が台帳と
        // 突き合わせて ABAC で絞り直す（[[IADR-0335]]）。**並びは後段が保つ関連度順**である。
        // 🔴 **`/pages/{slug}` より先に登録する必要は無い**（前置が違うので衝突しない）が、
        // 経路の並びは UC-07 の逐語（開く／検索する）および後段 `WikiEndpoints` と同じ順にしておく。
        g.MapGet("/search", (string? q, int? limit,
            IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            ProxyAsync(f, h, BuildSearchPath(q, limit), ct))
            .WithName("BffWikiSearch");

        // FR-13, UC-07: 個別取得（slug）。後段は ABAC 通過時のみ Wiki.js の描画結果をプロキシし、
        // 権限外・不存在・アーカイブ済みはいずれも 404（存在秘匿）。**その 404 をそのまま返す。**
        g.MapGet("/pages/{slug}", (string slug,
            IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            ProxyAsync(f, h, $"/wiki/pages/{Uri.EscapeDataString(slug)}", ct))
            .WithName("BffWikiPageBySlug");

        // FR-13, UC-07, SC-03: 個別取得（文書 ID）。**文書詳細から Wiki 本文へ渡る導線が使う。**
        //
        // 🔴 **`{documentId:guid}` の制約を後段と揃える。** BFF で `string` にすると形式不正の 400 の
        // 出所が 2 か所になる（`GraphBffEndpoints` の `{documentId:guid}` と同じ扱い）。
        // **`/pages/{slug}` とは衝突しない** —— セグメント数が違う（`pages/x` と `pages/by-doc/x`）。
        g.MapGet("/pages/by-doc/{documentId:guid}", (Guid documentId,
            IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            ProxyAsync(f, h, $"/wiki/pages/by-doc/{documentId}", ct))
            .WithName("BffWikiPageByDocument");

        return app;
    }

    // 上の 6: **指定されたものだけ**を後段のクエリへ載せる。
    // 既定値を BFF が埋めると、後段の既定（20）・上限（50）と二重管理になる。
    // 空白だけの `q` も**そのまま渡す** —— 「空なら 200 ＋ 空」を決めるのは後段である
    // （ここで短絡させると、後段を変えたときに BFF だけ古い判定で応答する）。
    private static string BuildSearchPath(string? q, int? limit)
    {
        var parts = new List<string>(2);
        if (q is not null)
            parts.Add($"q={Uri.EscapeDataString(q)}");
        if (limit is not null)
            parts.Add($"limit={limit.Value}");

        return parts.Count == 0 ? "/wiki/search" : $"/wiki/search?{string.Join('&', parts)}";
    }

    // 後段（WikiService）へ中継し、応答（状態・Content-Type・本文）をそのまま返す。
    //
    // 🔴 上の 4: **状態コードを作り替えない。** とくに 404（存在秘匿）と 502（Wiki.js 不達）は
    // 意味が違うので畳まない。上の 5: 後段不達も 502 とし、空の 200 で隠さない。
    private static async Task<IResult> ProxyAsync(
        IHttpClientFactory httpFactory, HttpContext http, string path, CancellationToken ct)
    {
        var client = httpFactory.CreateClient(ClientName);
        using var req = new HttpRequestMessage(HttpMethod.Get, path);

        // 🔴 上の 2: 資格情報の転送。**これが権限判定の入力そのものである。**
        var auth = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
            req.Headers.TryAddWithoutValidation("Authorization", auth);

        try
        {
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var contentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
            return Results.Content(body, contentType, statusCode: (int)resp.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }
}
