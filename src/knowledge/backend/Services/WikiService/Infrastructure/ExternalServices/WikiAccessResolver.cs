using WikiService.Domain.Ports;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Authz;
using System.Net.Http.Json;

namespace WikiService.Infrastructure.ExternalServices;

// FR-13, FR-05, UC-07, ADR-0011, ADR-0004: AuthorizationService の /authz/scope を呼び出し、
// 閲覧要求元の ABAC 許可スコープを解決する。
// 認可サービス障害時も deny-by-default（Granted=false）へ縮退し 500 を伝播させない
// （RagOrchestrator.ResolveScopeAsync と同一方針）。
// FR-13, FR-05, NFR-09, ADR-0029, ADR-0075, [[IADR-0379]] 決定 5, [[IADR-0401]] 決定 1 (#1255):
// **gRPC 経路との並走。** `Services:AuthorizationServiceGrpc` が構成されて `AuthzScopeGrpcClient` が
// DI に在れば gRPC で解決し、無ければ従来どおり REST で解決する。**並走中の正は REST**（gRPC は opt-in）。
// どちらの経路も同じ deny-by-default（`Granted=false`）へ縮退する。
// 🔴 **未認証の短絡は輸送の手前にある**（下の `IsAuthenticated` 判定）—— gRPC でも
// **匿名では 1 度も呼ばない**。既定 null は既存テストの直接構築を壊さないためである。
public class WikiAccessResolver(
    IHttpClientFactory httpFactory,
    AuthzScopeGrpcClient? authzScopeGrpc = null) : IWikiAccessResolver
{
    // UC-07 事前条件「**認証済み**」（#1126 / IADR-0335）。**未認証は認可サービスを呼ばずに拒否する。**
    // 匿名でも到達し得る要求へ与える身元。**認可サービスへは渡らない**（この値で問い合わせない）。
    private const string AnonymousUserId = "anonymous";

    // FR-05, [[IADR-0272]] 決定 4, [[IADR-0401]] 決定 1 (#1255): 本サービスが解決するアクション。
    // 閲覧経路しか持たないので read である。REST 側は `AccessScopeRequest.Action` の既定値に
    // 頼っているが、**gRPC 側では明示して渡す**（既定への依存を輸送ごとに隠さない）。
    private const string ScopeAction = "read";

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

        // 🔴 gRPC 経路も**この短絡の後**にある（上の未認証判定を通った要求だけが後段へ届く）。
        // action は現行と同じ read（`AccessScopeRequest.Action` の既定値）を明示して渡す。
        if (authzScopeGrpc is not null)
            return await authzScopeGrpc.ResolveScopeAsync(userId, userAttrs, ScopeAction, ct);

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

    // FR-05, ADR-0080, IADR-0411 (#1323): 抽出はプラットフォーム唯一の点へ委譲する。
    // 🔴 **ここで読むキーを列挙しない。** 同じ列挙が 6 か所に散っていたことが #1323 の欠陥であり、
    // 1 か所でも取り残すとその経路だけ判定が変わる。集合値（`tags` / `projects`）の符号化も
    // 共有点が持つ（`UserAttributeEncoding`）。
    private static Dictionary<string, string> ExtractUserAttributes(HttpContext ctx)
        => BffScopeResolver.ExtractUserAttributes(ctx);
}
