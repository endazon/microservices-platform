using KnowledgePlatform.Shared.Contracts.Dtos;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Extensions;

namespace KnowledgePlatform.Bff.Foundation.Endpoints;

// FR-09, UC-05, SC-09, IADR-0040: 管理者設定（ABAC）の BFF 集約。
// AuthorizationService の管理 API（/authz/policies・/authz/attributes）へ AdminOnly で中継する。
// AuthorizationService 側も同ロール（AdminOnly）を強制するため、利用者の資格情報を後段へ伝播する。
// 応答は透過（passthrough）し、保存前検証エラー（400 ValidationProblem）・属性参照競合（409）・
// 不在（404）をそのまま SPA へ返す（検証結果・矛盾表示を画面が再現できるようにする）。
public static class AuthzBffEndpoints
{
    public static IEndpointRouteBuilder MapAuthzBffEndpoints(this IEndpointRouteBuilder app)
    {
        // SC-09/IADR-0040: 管理者設定（ABAC）は platform-admin のみ（Issue #135 の明示）。
        var g = app.MapGroup("/bff/admin/authz")
            .WithTags("Authz BFF")
            .RequireAuthorization(KnowledgePlatformAuthPolicies.AdminOnly);

        // ---- FR-09, UC-05: ABAC ポリシー管理 ----
        g.MapGet("/policies", (IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            Proxy(f, h, HttpMethod.Get, "/authz/policies", ct))
            .WithName("BffAuthzListPolicies").Produces<List<AbacPolicyDto>>();

        g.MapGet("/policies/{id:guid}", (Guid id, IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            Proxy(f, h, HttpMethod.Get, $"/authz/policies/{id}", ct))
            .WithName("BffAuthzGetPolicy").Produces<AbacPolicyDto>();

        // 登録・更新は保存前に矛盾検証される。検証エラー（400）は透過する。
        g.MapPost("/policies", (IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            Proxy(f, h, HttpMethod.Post, "/authz/policies", ct))
            .WithName("BffAuthzCreatePolicy");

        g.MapPut("/policies/{id:guid}", (Guid id, IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            Proxy(f, h, HttpMethod.Put, $"/authz/policies/{id}", ct))
            .WithName("BffAuthzUpdatePolicy");

        // 有効／無効の切替（削除せず一時停止）。
        g.MapPatch("/policies/{id:guid}/active", (Guid id, IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            Proxy(f, h, HttpMethod.Patch, $"/authz/policies/{id}/active", ct))
            .WithName("BffAuthzSetPolicyActive");

        g.MapDelete("/policies/{id:guid}", (Guid id, IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            Proxy(f, h, HttpMethod.Delete, $"/authz/policies/{id}", ct))
            .WithName("BffAuthzDeletePolicy");

        // ---- FR-09, UC-05: 属性辞書管理 ----
        g.MapGet("/attributes", (IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            Proxy(f, h, HttpMethod.Get, "/authz/attributes", ct))
            .WithName("BffAuthzListAttributes").Produces<List<AttributeDefinitionDto>>();

        g.MapGet("/attributes/{id:guid}", (Guid id, IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            Proxy(f, h, HttpMethod.Get, $"/authz/attributes/{id}", ct))
            .WithName("BffAuthzGetAttribute").Produces<AttributeDefinitionDto>();

        g.MapPost("/attributes", (IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            Proxy(f, h, HttpMethod.Post, "/authz/attributes", ct))
            .WithName("BffAuthzCreateAttribute");

        g.MapPut("/attributes/{id:guid}", (Guid id, IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            Proxy(f, h, HttpMethod.Put, $"/authz/attributes/{id}", ct))
            .WithName("BffAuthzUpdateAttribute");

        // 参照中の属性削除は 409 で拒否される（IADR-0006）。透過する。
        g.MapDelete("/attributes/{id:guid}", (Guid id, IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            Proxy(f, h, HttpMethod.Delete, $"/authz/attributes/{id}", ct))
            .WithName("BffAuthzDeleteAttribute");

        return app;
    }

    // AuthorizationService へ透過中継する。要求本文（書き込み時）と Authorization を後段へ引き継ぎ、
    // 応答は status・content-type・本文をそのまま返す（検証 400・競合 409・不在 404 を保つ）。
    // 応答本文は ReadAsStringAsync で一括読み込みする。ABAC の属性辞書・ポリシーは管理系の小さな
    // ペイロード（件数・サイズとも小規模）を前提とするため、ストリーミング転送は導入しない（YAGNI。
    // 将来ペイロードが大きくなれば Results.Stream 等へ切替を検討する。レビュー #170 指摘対応）。
    private static async Task<IResult> Proxy(
        IHttpClientFactory httpFactory, HttpContext http, HttpMethod method, string path, CancellationToken ct)
    {
        var client = httpFactory.CreateClient("AuthorizationService");
        using var req = new HttpRequestMessage(method, path);

        var auth = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
            req.Headers.TryAddWithoutValidation("Authorization", auth);

        // 書き込みメソッドは要求本文を後段へ引き継ぐ（content-type 保持）。
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
