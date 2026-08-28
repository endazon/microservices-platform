using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace Platform.Bff.Foundation.Endpoints;

// FR-16, UC-09, SC-12, ADR-0024: MCP クライアント登録管理の BFF 集約。
// McpServer の管理 API（/mcp-clients*）へ AdminOnly で中継する。
// 後段（McpServer）も同じ AdminOnly を強制するため、利用者の資格情報を後段へ伝播する
// （BFF・後段の二重ゲート。IADR-0044 の多層防御）。
//
// 🔴 **応答は透過（passthrough）する。** 状態コードを作り替えない ——
// 後段の 404（不在）を 403 や 200 へ変換すると、**存在秘匿が BFF 層で破れる**か、
// 逆に「無い」を「拒否された」と読ませることになる。検証エラー（400 ValidationProblem）も
// そのまま SPA へ返し、画面が拒否理由（個人資料を読ませる属性割当の禁止等）を表示できるようにする。
//
// 経路は AuthzBffEndpoints（/bff/admin/authz）と同型である。両者は別の後段を持つが、
// 「管理 API を AdminOnly で透過中継する」という形は同じなので、意図的に同じ書き方に揃えてある。
public static class McpClientBffEndpoints
{
    /// <summary>後段（McpServer）の named HttpClient 名。Program.cs の登録と一致させる。</summary>
    public const string ClientName = "McpServer";

    public static IEndpointRouteBuilder MapMcpClientBffEndpoints(this IEndpointRouteBuilder app)
    {
        // 05_screens §共通シェル「SC-09・SC-12・SC-17 = システム管理者」。運用者も不可。
        var g = app.MapGroup("/bff/admin/mcp-clients")
            .WithTags("McpClients BFF")
            .RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        // SC-12 主要素 1: 登録クライアント一覧（有人／サービスアカウント種別・状態）。
        g.MapGet("", (IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            Proxy(f, h, HttpMethod.Get, "/mcp-clients", ct))
            .WithName("BffMcpListClients");

        // SC-12 主要素 2 / UC-09 基本フロー 1: クライアント登録（有人 / 無人）。
        // 後段は種別・越境ティアの値域と、**無人アカウントへの個人資料属性割当の禁止**（ADR-0034 決定 9）を
        // 400 ValidationProblem で拒む。**その 400 を透過する**（画面が理由を出す）。
        g.MapPost("", (IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            Proxy(f, h, HttpMethod.Post, "/mcp-clients", ct))
            .WithName("BffMcpRegisterClient");

        // SC-12 主要素 2 / UC-09: 無効化・再有効化。**次の呼び出しから即座に効く**（後段がキャッシュを挟まない）。
        g.MapPost("/{clientId}/disable", (string clientId, IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            Proxy(f, h, HttpMethod.Post, $"/mcp-clients/{Uri.EscapeDataString(clientId)}/disable", ct))
            .WithName("BffMcpDisableClient");

        g.MapPost("/{clientId}/enable", (string clientId, IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            Proxy(f, h, HttpMethod.Post, $"/mcp-clients/{Uri.EscapeDataString(clientId)}/enable", ct))
            .WithName("BffMcpEnableClient");

        // SC-12 主要素 3: 無人アカウントの ABAC 属性割当（機密区分上限・アクセス可能タグ）。
        g.MapPut("/{clientId}/attributes", (string clientId, IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            Proxy(f, h, HttpMethod.Put, $"/mcp-clients/{Uri.EscapeDataString(clientId)}/attributes", ct))
            .WithName("BffMcpReplaceClientAttributes");

        // SC-12 主要素 4: 公開ツール一覧（実効構成の参照）と構成ドリフト。
        // 🔴 **書き込みの口をここへ足さない。** 公開範囲の変更は Git 経由の公開構成変更で行う
        // （許可リスト方式・GitOps。05_screens §SC-12 アクション）。
        g.MapGet("/tools", (IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            Proxy(f, h, HttpMethod.Get, "/mcp-clients/tools", ct))
            .WithName("BffMcpListTools");

        return app;
    }

    // McpServer へ透過中継する。要求本文（書き込み時）と Authorization を後段へ引き継ぎ、
    // 応答は status・content-type・本文をそのまま返す（400 / 404 を保つ）。
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
