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
// FR-05, FR-17, UC-10, ADR-0034 決定 1, 計画 ADR-0086 決定 1, [[IADR-0410]] (#1255):
// 🔴 **入口は 2 つ・本体は 1 つである。** `ResolveAsync(HttpContext, …)` は north-south 由来の
// 検証済み `User` から、`ResolveForUserAsync(GraphUserContext, …)` は east-west gRPC の
// **本文で運ばれた利用者文脈**から解決する。どちらも同じ後段（`AuthzScope/Resolve`）を通り、
// **判定の位置は本サービスのままである**（`ADR-0034` 決定 1 のホップごと判定は満たされる ——
// 移行で変わるのは文脈の運び方だけである）。
public class GraphAccessResolver(
    IHttpClientFactory httpFactory,
    AuthzScopeGrpcClient? authzScopeGrpc = null) : IGraphAccessResolver
{
    // FR-05, FR-17, UC-10, ADR-0004, [[IADR-0044]], [[IADR-0335]] 決定 4 (#1318):
    // 🔴 **未認証の要求は、認可サービスへ問い合わせずに deny-by-default で返す。**
    // 短絡は `GraphUserContext.FromHttpContext` が立てる `IsAuthenticated` で表され、
    // **輸送の手前にある**（下の `ResolveForUserAsync` を参照）。
    //
    // 従前は未認証でも `anonymous` を身元として `/authz/scope` を叩いていた。認可側
    // （`AbacEvaluator`）は**利用者条件を持たないポリシーを全利用者にマッチさせる**ので、
    // そのようなポリシーが 1 件でも active なら**匿名にも許可が下りた**。
    //
    // 🔴 **Wiki（[[IADR-0335]]）とは前提が違う。違うので、そう書く。**
    // Wiki は `/wiki` 群にも各端点にも `RequireAuthorization` を持たず、匿名が**実際に到達
    // していた**。本サービスの消費者はすべて認証を要求しており、**現在の HTTP 表面から
    // ここへ匿名で到達する経路は無い**。本短絡が塞ぐのは「今漏れている穴」ではなく、
    // **fail-closed が端点ごとの `RequireAuthorization()` 宣言に依存している**こと自体である。
    //
    // **したがって匿名の状態コードは変えない。** 本サービスの匿名契約は従来どおり **401**
    // （ミドルウェアが弾く）であり、Wiki の 200 ＋ 空 / 404 とは別物である。
    public Task<AccessScopeResponse> ResolveAsync(
        HttpContext ctx, string action, CancellationToken ct = default)
        => ResolveForUserAsync(GraphUserContext.FromHttpContext(ctx), action, ct);

    // FR-05, FR-17, UC-10, NFR-09, ADR-0029, ADR-0034 決定 1, ADR-0075, 計画 ADR-0086 決定 1,
    // [[IADR-0379]] 決定 5, [[IADR-0401]] 決定 1, [[IADR-0410]] (#1255):
    // **利用者文脈から ABAC スコープを解決する唯一の本体。**
    //
    // 🔴 **REST 経路（`HttpContext`）と east-west gRPC 経路（本文の利用者文脈）が同じここを通る。**
    // 判定器を 2 つにしない —— 文脈の出所が違うだけで、**判定の位置は本サービスのまま**である
    // （`ADR-0034` 決定 1 のホップごと判定）。
    //
    // **gRPC 経路との並走。** `Services:AuthorizationServiceGrpc` が構成されて `AuthzScopeGrpcClient` が
    // DI に在れば gRPC で解決し、無ければ従来どおり REST で解決する。**並走中の正は REST**（gRPC は opt-in）。
    // どちらの経路も同じ deny-by-default（`Granted=false`）へ縮退する。
    // 🔴 利用者の JWT はメタデータへ載せない —— 載るのは本サービス自身の s2s トークンであり、
    // 利用者の文脈（userId / 属性 / **action**）は本文で運ぶ（docs/api/east-west-grpc.md §4）。
    // **`action` は既定値を持たない引数のまま gRPC へも明示して渡す**（[[IADR-0272]] 決定 4）。
    public async Task<AccessScopeResponse> ResolveForUserAsync(
        GraphUserContext user, string action, CancellationToken ct = default)
    {
        // 🔴 **短絡は輸送の手前にある** —— gRPC 経路でも**匿名では 1 度も呼ばない**。
        if (!user.IsAuthenticated)
            return new AccessScopeResponse(GraphUserContext.AnonymousUserId, [], false);

        var userId = user.UserId;
        var userAttrs = user.Attributes;

        if (authzScopeGrpc is not null)
            return await authzScopeGrpc.ResolveScopeAsync(userId, userAttrs, action, ct);

        var authzClient = httpFactory.CreateClient(AuthzScopeHttpClient.ClientName);
        try
        {
            var resp = await authzClient.PostAsJsonAsync("/authz/scope",
                new AccessScopeRequest(userId, new Dictionary<string, string>(userAttrs), action), ct);
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
}
