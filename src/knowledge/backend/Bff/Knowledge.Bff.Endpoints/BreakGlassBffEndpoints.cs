using Grpc.Core;
using Knowledge.Bff.Endpoints.Documents;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Platform.Shared.Infrastructure.Foundation.Observability;
using System.Net.Http;
using System.Net.Http.Json;

namespace Knowledge.Bff.Endpoints;

// FR-19, UC-11, SC-19, SC-03, 計画 ADR-0036 D-08, 08_data-egress-policy §経路 1, [[IADR-0610]], [[IADR-0611]]
// (#2046 の 2 本目): **緊急アクセス（Break-glass）の BFF の口**。後段は DocumentService の
// `/private-notes/{id}/break-glass*` と `/break-glass/audit`（1 本目）。planning#841 の裁定による本人同意型の管理者の閲覧である。
//
//   | 口                                                  | 誰が                                               |
//   | POST /bff/private-notes/{id}/break-glass            | 所有者本人のセッションだけ（発動）                 |
//   | POST /bff/private-notes/{id}/break-glass/end        | 所有者本人のセッションだけ（15 分より前の終了）    |
//   | GET  /bff/private-notes/{id}/break-glass            | 所有者本人のセッションだけ（状態・残り時間）       |
//   | GET  /bff/break-glass/audit                         | platform-admin（全件）と本人（自分の資料の行だけ） |
//
// ── 認可の姿（二重の守り）
//
// 1. **認証は必須・ロールは要求しない**（所有者が管理者でなくても発動できる。`/bff/*` の不変条件）。
// 2. 🔴 **機械の主体を通さない。** BFF の Bearer の腕は無人の主体だけを受理する（`BearerCallerPolicy`）ので、
//    `/bff/*` に機械が来ること自体はあり得る。発動・終了・状態は 404（存在ごと秘匿）、監査ログは 403（後段と同じ）。
// 3. 🔴 **発動・終了・状態は、BFF でも本人の資料かを確かめてから中継する**（後段の判定に加える 2 枚目。後段も台帳と
//    文書の `owner` の両方で本人かを判定する ＝ どちらか一方が壊れても他人の資料に届かない）。
//    確かめ方: 文書サービスの読み取りの rpc（`GetDocument`。**緊急アクセスの判定を含めない**形）で 1 件を引き、
//    個人資料であり `owner` がセッションの利用者名と一致することを見る。他人の・無い・個人資料でない・共有されただけ・
//    指名されただけは、いずれも後段へ送らず **404**（存在秘匿。403 は他人の ID の実在を漏らす）。
//    🔴 緊急アクセスを含めない形で引くのは、指名された管理者が本人の口を叩いたときに後段が「閲覧」を記録しないためである。
// 4. **発動だけ ABAC の `write` スコープで前段を絞る**（同じファイル群の個人資料の書き込み・共有の追加と同じ作法。
//    #1010 / [[IADR-0272]]）。🔴 **終了と状態には置かない** —— 終了は露出を縮める向きであり、ポリシーの有無で止めると
//    指名された管理者の閲覧が 15 分まで続く（端末の個別失効を write に依存させない理由と同じ。`PrivateNoteBffEndpoints` 冒頭の 5）。
// 5. 🔴 **主体は資格情報からしか採らない。** 本文・クエリに主体の口を作らない。後段へは利用者の資格情報を転送する
//    （後段は主体をトークンからしか採らない。BFF が別の資格情報で呼ぶ形へ変えてはならない）。
//
// ── 監査ログの読み取り（`/bff/break-glass/audit`）
// 誰の行まで読めるか（管理者は全件・他は本人の行だけ）は**後段が判定する**。BFF は人の主体であることだけを確かめる。
// BFF に本人の行の絞りを複写しない —— 管理者の判定を BFF の名前の比較で真似ると、名簿とずれたときに片方だけが緩む。
//
// ── 載せないもの
//   - 閲覧そのものの口は無い。指名された管理者は SC-03 の既存の口（`/bff/documents/{id}`・`/content`）で読む
//     （`DocumentBffEndpoints.FetchForViewAsync`。可否は後段の発動記録が決め、BFF は足さない）。
//   - 応答は後段の本文ごと透過する（400 の検証・409 の重ね発動・503 の名簿不達の文言が画面の根拠になる）。
//   - CSRF は全体のミドルウェア（`CsrfHeaderMiddleware`）が状態を変える動詞に効かせる。BFF にレート制限の仕組みは
//     現状無い（実測。本件で新設しない）。
public static class BreakGlassBffEndpoints
{
    public static IEndpointRouteBuilder MapBreakGlassBffEndpoints(this IEndpointRouteBuilder app)
    {
        var owner = app.MapGroup("/bff/private-notes/{id:guid}/break-glass").WithTags("PrivateNotes BFF")
            .RequireAuthorization();

        // ── SC-19: 発動（資料 1 件・理由必須・platform-admin を 1 人指名。15 分固定）────────────
        owner.MapPost("/", async (Guid id, ActivateBreakGlassRequest req, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
        {
            if (await EnsureOwnNoteAsync(id, http, ct) is { } denied) return denied;

            var scope = await BffScopeResolver.ResolveAsync(http, BffScopeAction.Write, ct);
            if (scope is null)
                return Results.Forbid(); // write 許可ポリシー無し＝発動不可（deny-by-default。上の 4）

            return await ForwardAsync(HttpMethod.Post, $"/private-notes/{id}/break-glass", req, httpFactory, http, ct);
        })
            .WithName("BffBreakGlassActivate")
            .Produces<BreakGlassGrantDto>(StatusCodes.Status201Created);

        // ── SC-19: 本人による 15 分より前の終了（終了日時は本人の終了か発動＋15 分の早いほう）──────
        owner.MapPost("/end", async (Guid id, IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
        {
            if (await EnsureOwnNoteAsync(id, http, ct) is { } denied) return denied;
            return await ForwardAsync(HttpMethod.Post, $"/private-notes/{id}/break-glass/end", null, httpFactory, http, ct);
        })
            .WithName("BffBreakGlassEnd")
            .Produces<BreakGlassGrantDto>();

        // ── SC-19: 状態（有効な発動があればその写し。残り時間の表示用 ＝ 許可の根拠にしない）──────
        owner.MapGet("/", async (Guid id, IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
        {
            if (await EnsureOwnNoteAsync(id, http, ct) is { } denied) return denied;
            return await ForwardAsync(HttpMethod.Get, $"/private-notes/{id}/break-glass", null, httpFactory, http, ct);
        })
            .WithName("BffBreakGlassStatus")
            .Produces<BreakGlassStatusResponse>();

        // ── 監査ログ（発動・終了・閲覧の 1 件ずつ。本文・題名は載らない）──────────────────
        // `documentId` / `limit` は型で受けてから組み立て直す（生のクエリ文字列を後段へ連結しない）。
        // `limit` の範囲（1〜200）は後段が 400 で返す —— BFF で丸めない・複写しない。
        app.MapGroup("/bff/break-glass").WithTags("PrivateNotes BFF").RequireAuthorization()
            .MapGet("/audit", async (Guid? documentId, int? limit, IHttpClientFactory httpFactory,
                HttpContext http, CancellationToken ct) =>
            {
                if (MachinePrincipal.IsMachine(http.User))
                    return Results.Forbid(); // 読むのは人である（後段と同じ。上の 2）

                var query = new List<string>();
                if (documentId is { } d) query.Add($"documentId={d:D}");
                if (limit is { } l) query.Add($"limit={l.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                var path = "/break-glass/audit" + (query.Count > 0 ? "?" + string.Join('&', query) : string.Empty);
                return await ForwardAsync(HttpMethod.Get, path, null, httpFactory, http, ct);
            })
            .WithName("BffBreakGlassAudit")
            .Produces<List<BreakGlassAuditEntryDto>>();

        return app;
    }

    // 上の 2・3: 人の主体で、資料が本人の個人資料のときだけ null（中継してよい）。それ以外は返すべき応答。
    // 🔴 「無い」と「他人の」は 404 で区別しない（存在秘匿）。**後段に届かない**ことだけは 502 で分ける
    //   （本人の資料が「無い」ように見えるのが最悪。`PrivateNoteBffEndpoints.ForwardAsync` と同じ向き）。
    internal static async Task<IResult?> EnsureOwnNoteAsync(Guid id, HttpContext http, CancellationToken ct)
    {
        if (MachinePrincipal.IsMachine(http.User) || string.IsNullOrWhiteSpace(http.User.Identity?.Name))
            return Results.NotFound();

        var grpc = http.RequestServices.GetRequiredService<DocumentReadGrpcClient>();
        DocumentDto? doc;
        try
        {
            // 🔴 緊急アクセスの判定を含めない形（`GetAsync`）で引く。所有者・共有先として読めるものだけが返る。
            doc = await grpc.GetAsync(http.User, id, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is (RpcException or InvalidOperationException))
        {
            DocumentReadFailureLog.Folded(http, "GetDocument", ex, "502");
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }

        return doc is not null && IsOwnPrivateNote(doc, http.User.Identity!.Name!) ? null : Results.NotFound();
    }

    // 個人資料（`doc_scope=private-note`）であり、`owner` がセッションの利用者名と一致する（大小を区別する。
    // 後段の所有者の判定 `DocumentBodyIntake.IsOwnedBy` と同じ比較）。
    internal static bool IsOwnPrivateNote(DocumentDto doc, string user)
        => doc.Attributes.TryGetValue("doc_scope", out var scope)
            && string.Equals(scope, "private-note", StringComparison.OrdinalIgnoreCase)
            && doc.Attributes.TryGetValue("owner", out var owner)
            && !string.IsNullOrWhiteSpace(owner)
            && string.Equals(owner, user, StringComparison.Ordinal);

    // 後段（DocumentService）へ利用者の資格情報で中継し、応答を本文ごと透過する。後段不達は 502。
    private static async Task<IResult> ForwardAsync(
        HttpMethod method, string path, object? body,
        IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct)
    {
        var client = httpFactory.CreateClient("DocumentService");
        var auth = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);

        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
            req.Content = JsonContent.Create(body, body.GetType());

        try
        {
            using var resp = await client.SendAsync(req, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NoContent)
                return Results.NoContent();
            var content = await resp.Content.ReadAsStringAsync(ct);
            var contentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
            return Results.Content(content, contentType, statusCode: (int)resp.StatusCode);
        }
        catch (Exception ex) when (ex is (HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            // NFR-19, [[IADR-0603]] (#2104): 畳む前に WARN と計器（後段名・経路・種別）。
            BffDownstreamFailure.Report(http, "DocumentService", ex, BffDownstreamFailure.Fallbacks.BadGateway);
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }
}
