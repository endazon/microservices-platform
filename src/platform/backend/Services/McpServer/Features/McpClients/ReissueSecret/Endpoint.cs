using McpServer.Domain;
using McpServer.Domain.Ports;
using McpServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace McpServer.Features.McpClients.ReissueSecret;

// FR-16, UC-09, SC-12, 計画 ADR-0134 決定 2 の 1・4・5・フォローアップ 4, [[IADR-0516]]（2026-10-09 追記 / #1845）:
// 無人の MCP クライアントの client secret の再発行。IdP（Keycloak）で regenerate し、**新しい値を応答で一度だけ返す**。
//
// ■ 🔴 **旧 secret はその時点で使えなくなる**（Keycloak 24 の client secret rotation〔preview〕は配備で無効なので猶予は無い。
//   ［#1859］26.7.4 でも regenerate の直後に旧 secret は 401 になる。手元の実測）。
//   画面は確認を挟む。漏えいに気づいたときの手段でもあるので、無効化された行でも再発行できる。
// ■ 🔴 **値は保存しない。** 登録簿は書かない（`updatedAt` も動かさない）。応答は `Cache-Control: no-store`。値はログにも監査にも出さない。
// ■ 表示と再発行はシステム管理者に限る（グループの既定 `AdminOnly`。決定 2 の 4）。
// ■ 入口が作っていないクライアント（印なし）・公開クライアント（有人）・IdP に無い行は、何も書かずに 400。
public static class ReissueMcpClientSecretEndpoint
{
    public static IEndpointRouteBuilder MapReissueMcpClientSecret(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{clientId}/reissue-secret", (string clientId, McpDbContext db, IServiceAccountProvisioner provisioner,
                IAuditLogger audit, HttpContext http, CancellationToken ct) =>
            McpClientAudit.RecordAsync(audit, http.User, McpClientAudit.SecretReissueAction, McpClientAudit.Detail(clientId),
                () => ReissueAsync(clientId, db, provisioner, http, ct)));

        return app;
    }

    private static async Task<IResult> ReissueAsync(
        string clientId, McpDbContext db, IServiceAccountProvisioner provisioner, HttpContext http, CancellationToken ct)
    {
        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId, ct);
        if (client is null) return Results.NotFound();

        // 有人は公開クライアント（PKCE）であり secret を持たない（計画 ADR-0134 決定 1）。IdP へ問い合わせる前に断る。
        if (client.Kind != McpClientKind.ServiceAccount)
            return McpClientEndpoints.Problem(
                $"クライアント '{clientId}' は有人（公開クライアント）であり、client secret を持ちません。");

        // 書き始める前の取り消しだけは受ける。regenerate は取り消しを伝えない（書き込みの口の規則）。
        ct.ThrowIfCancellationRequested();
        ClientSecretResult result;
        try
        {
            result = await provisioner.RegenerateClientSecretAsync(clientId, CancellationToken.None);
        }
        catch (IdpProvisioningException ex)
        {
            return ex.Failure == IdpProvisioningFailure.Unavailable
                ? Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "IdP への書き込み口が構成されていない")
                : Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway,
                    title: "IdP で client secret を再発行できなかった");
        }

        switch (result.Outcome)
        {
            case ClientSecretOutcome.Issued when result.Secret is { } secret:
                http.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new McpClientSecretView(clientId, secret.Reveal()));
            case ClientSecretOutcome.Absent:
                return McpClientEndpoints.Problem(
                    $"クライアント '{clientId}' は IdP（Keycloak）にありません（この入口ができる前の登録です）。"
                    + "属性を保存し直すと IdP に作られます。その後に再発行してください。");
            case ClientSecretOutcome.NotManaged:
                return McpClientEndpoints.Problem(
                    $"クライアント '{clientId}' は IdP（Keycloak）にこの画面を通らずに作られたものとして在ります。"
                    + "入口が作っていないクライアントの client secret は再発行しません。");
            default:
                return McpClientEndpoints.Problem(
                    $"クライアント '{clientId}' は IdP（Keycloak）で機密クライアントではないため、client secret を再発行できません。");
        }
    }
}
