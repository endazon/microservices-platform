using GraphService.Domain.Ports;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Authz;
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
// FR-17, FR-05, NFR-09, ADR-0029, ADR-0075, [[IADR-0379]] 決定 5, [[IADR-0401]] 決定 1 (#1255):
// **gRPC 経路との並走。** `Services:AuthorizationServiceGrpc` が構成されて `AuthzScopeGrpcClient` が
// DI に在れば gRPC で解決し、無ければ従来どおり REST で解決する。**並走中の正は REST**（gRPC は opt-in）。
// どちらの経路も同じ deny-by-default（`Granted=false`）へ縮退する。
// 🔴 利用者の JWT はメタデータへ載せない —— 載せるのは本サービス自身の s2s トークンであり、
// 利用者の文脈（userId / 属性 / **action**）は本文で運ぶ（docs/api/east-west-grpc.md §4）。
// **`action` は既定値を持たない引数のまま gRPC へも明示して渡す**（[[IADR-0272]] 決定 4）。
// 既定 null は既存テストの直接構築（`new GraphAccessResolver(factory)`）を壊さないためである。
// FR-05, [[IADR-0044]], [[IADR-0335]] 決定 4 (#1318):
// 🔴 **未認証の短絡は輸送の手前にある**（下の `IsAuthenticated` 判定）—— gRPC でも
// **匿名では 1 度も呼ばない**。状態コードは変えない（本サービスの匿名契約は 401 のまま）。
public class GraphAccessResolver(
    IHttpClientFactory httpFactory,
    AuthzScopeGrpcClient? authzScopeGrpc = null) : IGraphAccessResolver
{
    // 匿名でも到達し得る要求へ与える身元。**認可サービスへは渡らない**（この値で問い合わせない）。
    private const string AnonymousUserId = "anonymous";

    public async Task<AccessScopeResponse> ResolveAsync(
        HttpContext ctx, string action, CancellationToken ct = default)
    {
        // 🔴 FR-05, FR-17, UC-10, ADR-0004, [[IADR-0044]]（多層防御）, [[IADR-0335]] 決定 4 (#1318):
        // **未認証の要求は、認可サービスへ問い合わせずに deny-by-default で返す。**
        //
        // 従前は未認証でも `anonymous` を身元として `/authz/scope` を叩いていた。認可側
        // （`AbacEvaluator`）は**利用者条件を持たないポリシーを全利用者にマッチさせる**ので、
        // そのようなポリシーが 1 件でも active なら**匿名にも許可が下りた**。
        //
        // 🔴 **Wiki（[[IADR-0335]]）とは前提が違う。違うので、そう書く。**
        // Wiki は `/wiki` 群にも各端点にも `RequireAuthorization` を持たず、匿名が**実際に到達
        // していた**。本サービスの消費者はすべて認証を要求しており（`/graph/suggestions` 群と
        // `GetNode` / `Neighbors` / `CreateEdge` / `edge-types`）、**現在の HTTP 表面から
        // ここへ匿名で到達する経路は無い**。本短絡が塞ぐのは「今漏れている穴」ではなく、
        // **fail-closed が端点ごとの `RequireAuthorization()` 宣言に依存している**こと自体である
        // —— 1 個書き忘れた端点が足された瞬間、その端点は `anonymous` で ABAC を通る。
        //
        // **したがって匿名の状態コードは変えない。** 本サービスの匿名契約は従来どおり **401**
        // （ミドルウェアが弾く）であり、Wiki の 200 ＋ 空 / 404 とは別物である。
        //
        // 🔴 **短絡は輸送の手前にある** —— gRPC 経路でも**匿名では 1 度も呼ばない**。
        if (ctx.User.Identity?.IsAuthenticated != true)
            return new AccessScopeResponse(AnonymousUserId, [], false);

        var userId = ctx.User.Identity.Name ?? AnonymousUserId;
        var userAttrs = ExtractUserAttributes(ctx);

        if (authzScopeGrpc is not null)
            return await authzScopeGrpc.ResolveScopeAsync(userId, userAttrs, action, ct);

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
