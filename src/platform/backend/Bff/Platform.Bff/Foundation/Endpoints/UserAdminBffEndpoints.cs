using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace Platform.Bff.Foundation.Endpoints;

// FR-05, FR-09, UC-05, SC-17, ADR-0026, IADR-0301: 利用者アカウント管理の BFF 集約。
// AuthorizationService の管理 API（/authz/users*）へ AdminOnly で中継する。
// 後段も同じ AdminOnly を強制するため、利用者の資格情報を後段へ伝播する
// （BFF・後段の二重ゲート。IADR-0044 の多層防御）。
//
// 🔴 **応答は透過（passthrough）する。状態コードを作り替えない。**
// 検証エラー（400 ValidationProblem）は画面が拒否理由を出せるようにそのまま返し、
// 不在（404）も 403 や 200 へ変換しない。経路の形は `AuthzBffEndpoints` /
// `McpClientBffEndpoints` と**意図的に同じ**に揃えてある。
//
// 🔴 **新規作成の口を持たない。** 計画 05_screens §SC-17 アクション
// 「本画面から新規作成はしない」。後段にも無く、ここにも作らない（二重に閉じる）。
public static class UserAdminBffEndpoints
{
    /// <summary>後段（AuthorizationService）の named HttpClient 名。Program.cs の登録と一致させる。</summary>
    public const string ClientName = "AuthorizationService";

    public static IEndpointRouteBuilder MapUserAdminBffEndpoints(this IEndpointRouteBuilder app)
    {
        // 05_screens §共通シェル「SC-09・SC-12・SC-17 = システム管理者」。運用者も不可。
        var g = app.MapGroup("/bff/admin/users")
            .WithTags("UserAdmin BFF")
            .RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        // SC-17 主要素 1: 利用者一覧（部門・ロール・ABAC 属性・状態）。
        g.MapGet("", (IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            Proxy(f, h, HttpMethod.Get, "/authz/users", ct))
            .WithName("BffUserAdminListUsers").Produces<List<PlatformUserDto>>();

        // SC-17 入力規則「定義済みロールのみ」の値域。**画面はこれを引いて選択肢を作る。**
        g.MapGet("/assignable-roles", (IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            Proxy(f, h, HttpMethod.Get, "/authz/users/assignable-roles", ct))
            .WithName("BffUserAdminListAssignableRoles").Produces<List<string>>();

        // SC-17: ABAC 属性の割当（差し替え）。辞書外の値・必須欠落は後段が 400 で拒む。
        g.MapPut("/{userId}/attributes", (string userId, IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            Proxy(f, h, HttpMethod.Put, $"/authz/users/{Uri.EscapeDataString(userId)}/attributes", ct))
            .WithName("BffUserAdminReplaceUserAttributes");

        // SC-17: ロール割当（差し替え。併任可）。空集合・定義外ロールは後段が 400 で拒む。
        g.MapPut("/{userId}/roles", (string userId, IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            Proxy(f, h, HttpMethod.Put, $"/authz/users/{Uri.EscapeDataString(userId)}/roles", ct))
            .WithName("BffUserAdminReplaceUserRoles");

        // SC-17 アクション「無効化→全セッション即時失効」。**2 つで 1 つの操作**なので口も 1 つにする。
        g.MapPost("/{userId}/disable", (string userId, IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            Proxy(f, h, HttpMethod.Post, $"/authz/users/{Uri.EscapeDataString(userId)}/disable", ct))
            .WithName("BffUserAdminDisableUser");

        g.MapPost("/{userId}/enable", (string userId, IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            Proxy(f, h, HttpMethod.Post, $"/authz/users/{Uri.EscapeDataString(userId)}/enable", ct))
            .WithName("BffUserAdminEnableUser");

        return app;
    }

    // AuthorizationService へ透過中継する。要求本文（書き込み時）と Authorization を後段へ引き継ぎ、
    // 応答は status・content-type・本文をそのまま返す（検証 400・不在 404 を保つ）。
    // 応答本文は一括読み込みする（管理系の小さなペイロードを前提とする。AuthzBffEndpoints と同じ判断）。
    private static async Task<IResult> Proxy(
        IHttpClientFactory httpFactory, HttpContext http, HttpMethod method, string path, CancellationToken ct)
    {
        var client = httpFactory.CreateClient(ClientName);
        using var req = new HttpRequestMessage(method, path);

        var auth = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
            req.Headers.TryAddWithoutValidation("Authorization", auth);

        if (method != HttpMethod.Get && method != HttpMethod.Delete)
        {
            var content = new StreamContent(http.Request.Body);
            var contentType = http.Request.ContentType ?? "application/json";
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            req.Content = content;
        }

        try
        {
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var respContentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
            return Results.Content(body, respContentType, statusCode: (int)resp.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // 後段不達は 502（管理 API のため存在秘匿は不要。呼び出し側で再試行できる）。
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }
}
