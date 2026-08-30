using McpServer.Domain;
using McpServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace McpServer.Features.McpClients.RegisterClient;

// FR-16, UC-09 基本フロー 1, SC-12: クライアント登録（有人 / サービスアカウント）。
public static class RegisterMcpClientEndpoint
{
    public static IEndpointRouteBuilder MapRegisterMcpClient(this IEndpointRouteBuilder app)
    {
        app.MapPost("", async (RegisterMcpClientRequest req, McpDbContext db, TimeProvider clock) =>
        {
            if (string.IsNullOrWhiteSpace(req.ClientId))
                return McpClientEndpoints.Problem("clientId は必須です。");
            if (!TryParseKind(req.Kind, out var kind))
                return McpClientEndpoints.Problem(
                    $"kind の値 '{req.Kind}' は不正です（interactive / service-account）。");
            if (!TryParseTier(req.EgressTier, out var tier))
                return McpClientEndpoints.Problem($"egressTier の値 '{req.EgressTier}' は不正です。");

            var attributes = req.Attributes ?? [];

            // 🔴 ADR-0024（2026-08-02 注記）/ ADR-0034 決定 9:
            // サービスアカウントへ個人資料を読ませる属性割当は禁止する。
            // 構成（公開構成のスキーマ検証）と API の**両方**で弾く。検証関数は 1 つを共用する。
            if (kind == McpClientKind.ServiceAccount)
            {
                var errors = ToolPublicationConfigValidator
                    .ValidateServiceAccountAttributes(req.ClientId, attributes);
                if (errors.Count > 0) return McpClientEndpoints.Problem(errors[0]);
            }

            if (await db.Clients.AnyAsync(c => c.ClientId == req.ClientId))
                return McpClientEndpoints.Problem($"クライアント '{req.ClientId}' は既に登録されています。");

            var client = McpClient.Register(
                req.ClientId, req.DisplayName, kind, attributes, tier, clock.GetUtcNow());
            db.Clients.Add(client);
            await db.SaveChangesAsync();
            return Results.Created($"/mcp-clients/{client.ClientId}", McpClientEndpoints.ToView(client));
        });

        return app;
    }

    private static bool TryParseKind(string? value, out McpClientKind kind)
    {
        kind = McpClientKind.Interactive;
        switch (value?.Trim().ToLowerInvariant())
        {
            case "interactive": kind = McpClientKind.Interactive; return true;
            case "service-account": kind = McpClientKind.ServiceAccount; return true;
            default: return false;
        }
    }

    private static bool TryParseTier(string? value, out EgressTier tier)
    {
        // 未指定は最も低い保護水準（＝本文を出しにくい側）へ倒す。08_data-egress-policy §基本原則。
        tier = EgressTier.StandardExternal;
        if (string.IsNullOrWhiteSpace(value)) return true;
        switch (value.Trim().ToLowerInvariant())
        {
            case "self-hosted": tier = EgressTier.SelfHosted; return true;
            case "protected-external": tier = EgressTier.ProtectedExternal; return true;
            case "standard-external": tier = EgressTier.StandardExternal; return true;
            default: return false;
        }
    }
}
