using System.Net.Http.Json;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Authz;
using RetrievalService.Domain.Ports;

namespace RetrievalService.Infrastructure.ExternalServices;

// FR-03, FR-04, FR-05, NFR-09, UC-01, SC-01, SC-08, ADR-0004, ADR-0029, ADR-0034 決定 1,
// ADR-0075, ADR-0080, [[IADR-0044]], [[IADR-0272]] 決定 4, [[IADR-0379]] 決定 5,
// [[IADR-0401]] 決定 1, [[IADR-0410]], [[IADR-0411]], [[IADR-0416]] (#1339):
// AuthorizationService の `/authz/scope` を呼び、**本サービスが自分で**許可スコープを解決する。
//
// **`WikiAccessResolver` と同型である。** 違うのは呼ぶ側の名前だけであり、
// 短絡・縮退・並走のどれも**同じ形**にしてある —— 形を変えると、どちらが正しいのか
// 読む人が判断できなくなる。
//
// 🔴 **未認証は認可サービスへ問い合わせずに deny する**（[[IADR-0044]] の多層防御 /
// [[IADR-0335]] 決定 4）。問い合わせると、**利用者条件を持たないポリシーが 1 件でも active なら
// 匿名にも許可が下りる**（`AbacEvaluator` は条件が空なら全利用者にマッチする。FR-05 の意図）。
//
// 🔴 **状態コードは変えない。** `/search` は認可を持たない（#1318 欠陥 B）——
// **本 PR で認可は掛けない**（掛けると SPA / McpServer への影響を測る必要がある）。
// 未認証は 401 ではなく**空応答へ倒れる**（存在秘匿。現行の契約のまま）。
//
// 🔴 **利用者の JWT はメタデータへ載せない**（gRPC 経路）—— 載るのは本サービス自身の
// s2s トークンであり、利用者の文脈（userId / 属性 / action）は本文で運ぶ。
public sealed class SearchAccessResolver(
    IHttpClientFactory httpFactory,
    AuthzScopeGrpcClient? authzScopeGrpc = null) : ISearchAccessResolver
{
    /// <summary>未認証のときに応答へ載せる利用者 ID（判定には使わない）。</summary>
    private const string AnonymousUserId = "anonymous";

    // FR-05, [[IADR-0272]] 決定 4: 本サービスが解決するアクション。
    // 検索・属性値照会は閲覧経路しか持たないので read である。**既定へ頼らず明示する。**
    private const string ScopeAction = "read";

    public async Task<AccessScopeResponse> ResolveAsync(HttpContext ctx, CancellationToken ct = default)
    {
        // 🔴 **未認証は問い合わせずに deny**（上の 🔴 を参照）。
        if (ctx.User.Identity?.IsAuthenticated != true)
            return new AccessScopeResponse(AnonymousUserId, [], false);

        return await ResolveForUserAsync(
            ctx.User.Identity.Name ?? AnonymousUserId, ExtractUserAttributes(ctx), ct);
    }

    // FR-05, NFR-16, ADR-0086 決定 1, [[IADR-0410]], [[IADR-0417]] (#1255):
    // 🔴 **入口は 2 つ・本体はこの 1 つである。** REST は検証済みの `User` から、
    // gRPC は**本文で運ばれた利用者文脈**から入る —— どちらも同じ後段を通る。
    public async Task<AccessScopeResponse> ResolveForUserAsync(
        string userId, IReadOnlyDictionary<string, string> attributes, CancellationToken ct = default)
    {
        var userAttrs = attributes as Dictionary<string, string> ?? new Dictionary<string, string>(attributes);

        // 🔴 **並走中の正は REST** である（[[IADR-0379]] 決定 5）。
        if (authzScopeGrpc is not null)
            return await authzScopeGrpc.ResolveScopeAsync(userId, userAttrs, ScopeAction, ct);

        var authzClient = httpFactory.CreateClient(AuthzScopeHttpClient.ClientName);
        try
        {
            var resp = await authzClient.PostAsJsonAsync("/authz/scope",
                new AccessScopeRequest(userId, userAttrs, ScopeAction), ct);
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

    // FR-05, ADR-0080, [[IADR-0411]] (#1323): 抽出はプラットフォーム唯一の点へ委譲する。
    // 🔴 **ここで読むキーを列挙しない。** 同じ列挙が 6 か所に散っていたことが #1323 の欠陥である。
    private static Dictionary<string, string> ExtractUserAttributes(HttpContext ctx)
        => BffScopeResolver.ExtractUserAttributes(ctx);
}
