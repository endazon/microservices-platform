using McpServer.Domain;
using McpServer.Domain.Ports;
using McpServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace McpServer.Features.McpClients.ReplaceAttributes;

// FR-16, UC-09 基本フロー 1: 無人アカウントへの ABAC 属性割当（差し替え）。
public static class ReplaceMcpClientAttributesEndpoint
{
    public static IEndpointRouteBuilder MapReplaceMcpClientAttributes(this IEndpointRouteBuilder app)
    {
        app.MapPut("/{clientId}/attributes",
            (string clientId, ReplaceMcpClientAttributesRequest req,
                   McpDbContext db, TimeProvider clock,
                   IRegistrarAttributeResolver registrar, IServiceAccountProvisioner provisioner,
                   ILoggerFactory loggers, IAuditLogger audit, HttpContext http, CancellationToken ct) =>
            // FR-16, SC-12, 計画 ADR-0134 フォローアップ 5（#1845）: 結果を監査に残す（割り当てた属性も。値は ABAC の属性値で秘密ではない）。
            McpClientAudit.RecordAsync(audit, http.User, McpClientAudit.ReplaceAttributesAction,
                McpClientAudit.Detail(clientId, attributes: req.Attributes),
                () => ReplaceAsync(clientId, req, db, clock, registrar, provisioner, loggers, ct)));

        return app;
    }

    private static async Task<IResult> ReplaceAsync(
        string clientId, ReplaceMcpClientAttributesRequest req, McpDbContext db, TimeProvider clock,
        IRegistrarAttributeResolver registrar, IServiceAccountProvisioner provisioner,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var client = await db.Clients.FirstOrDefaultAsync(c => c.ClientId == clientId, ct);
        if (client is null) return Results.NotFound();

        // 🔴 ADR-0034 決定 9 / ADR-0062 決定 2・3: 登録経路と**同じ 1 つの関数**が判定する。
        // 登録だけ塞いで差し替えが緩い形にしない（片方だけ直したときに黙ってズレる）。
        var rejected = await McpClientEndpoints.RejectUnassignableAsync(
            clientId, client.Kind, req.Attributes, registrar, ct);
        if (rejected is not null) return rejected;

        async Task<IResult> WriteRegistry(CancellationToken token)
        {
            client.ReplaceAttributes(req.Attributes, clock.GetUtcNow());
            await db.SaveChangesAsync(token);
            return Results.Ok(McpClientMapper.ToView(client));
        }

        if (client.Kind != McpClientKind.ServiceAccount) return await WriteRegistry(ct);

        // 🔴 FR-16, SC-12, 計画 ADR-0123 決定 2・3, [[IADR-0516]] 決定 4 (#1786): 差し替えも **検証 → IdP → 登録簿**。
        // IdP にクライアントが無い行（本入口ができる前の登録）は、ここで IdP へ載る。
        // 登録簿で無効化された行は IdP にも無効のまま作る（有効なクライアントを生まない。[[IADR-0516]] 決定 4）。
        return await IdpFirstWrite.RunAsync(
            token => provisioner.ReplaceAttributesAsync(clientId, client.DisplayName, req.Attributes, client.Enabled, token),
            WriteRegistry, provisioner,
            loggers.CreateLogger(typeof(ReplaceMcpClientAttributesEndpoint)), ct);
    }
}
