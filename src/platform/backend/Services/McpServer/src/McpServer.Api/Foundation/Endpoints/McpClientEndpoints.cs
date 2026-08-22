using McpServer.Api.Foundation.Contracts;
using McpServer.Api.Foundation.Domain;
using McpServer.Api.Foundation.Persistence;
using McpServer.Api.Foundation.Services;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace McpServer.Api.Foundation.Endpoints;

// FR-16, UC-09, SC-12: MCP クライアント登録管理。**管理者限定**（SC-12「管理者限定」）。
public static class McpClientEndpoints
{
    public static IEndpointRouteBuilder MapMcpClientEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/mcp-clients").WithTags("McpClients")
            .RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        // UC-09 基本フロー 3: 登録クライアントの提示。
        g.MapGet("", async (McpDbContext db) =>
            Results.Ok((await db.Clients.AsNoTracking().OrderBy(c => c.ClientId).ToListAsync())
                .Select(ToView).ToList()));

        // UC-09 基本フロー 1: クライアント登録（有人 / サービスアカウント）。
        g.MapPost("", async (RegisterMcpClientRequest req, McpDbContext db, TimeProvider clock) =>
        {
            if (string.IsNullOrWhiteSpace(req.ClientId))
                return Problem("clientId は必須です。");
            if (!TryParseKind(req.Kind, out var kind))
                return Problem($"kind の値 '{req.Kind}' は不正です（interactive / service-account）。");
            if (!TryParseTier(req.EgressTier, out var tier))
                return Problem($"egressTier の値 '{req.EgressTier}' は不正です。");

            var attributes = req.Attributes ?? [];

            // 🔴 ADR-0024（2026-08-02 注記）/ ADR-0034 決定 9:
            // サービスアカウントへ個人資料を読ませる属性割当は禁止する。
            // 構成（公開構成のスキーマ検証）と API の**両方**で弾く。検証関数は 1 つを共用する。
            if (kind == McpClientKind.ServiceAccount)
            {
                var errors = ToolPublicationConfigValidator
                    .ValidateServiceAccountAttributes(req.ClientId, attributes);
                if (errors.Count > 0) return Problem(errors[0]);
            }

            if (await db.Clients.AnyAsync(c => c.ClientId == req.ClientId))
                return Problem($"クライアント '{req.ClientId}' は既に登録されています。");

            var client = McpClient.Register(
                req.ClientId, req.DisplayName, kind, attributes, tier, clock.GetUtcNow());
            db.Clients.Add(client);
            await db.SaveChangesAsync();
            return Results.Created($"/mcp-clients/{client.ClientId}", ToView(client));
        });

        // UC-09 / SC-12: 無効化・再有効化。**次の呼び出しから即座に効く**
        // （McpSubjectResolver は毎回登録簿を引き、キャッシュを挟まない）。
        g.MapPost("/{clientId}/disable", (string clientId, McpDbContext db, TimeProvider clock) =>
            SetEnabledAsync(clientId, false, db, clock));

        g.MapPost("/{clientId}/enable", (string clientId, McpDbContext db, TimeProvider clock) =>
            SetEnabledAsync(clientId, true, db, clock));

        // UC-09 基本フロー 1: 無人アカウントへの ABAC 属性割当。
        g.MapPut("/{clientId}/attributes",
            async (string clientId, ReplaceMcpClientAttributesRequest req,
                   McpDbContext db, TimeProvider clock) =>
        {
            var client = await db.Clients.FirstOrDefaultAsync(c => c.ClientId == clientId);
            if (client is null) return Results.NotFound();

            if (client.Kind == McpClientKind.ServiceAccount)
            {
                var errors = ToolPublicationConfigValidator
                    .ValidateServiceAccountAttributes(clientId, req.Attributes);
                if (errors.Count > 0) return Problem(errors[0]);
            }

            client.ReplaceAttributes(req.Attributes, clock.GetUtcNow());
            await db.SaveChangesAsync();
            return Results.Ok(ToView(client));
        });

        // SC-12「公開ツール一覧の確認」/ ADR-0024 §5: 実効ツール一覧と構成ドリフト。
        g.MapGet("/tools", (ToolCatalog catalog) => Results.Ok(new EffectiveToolsView(
            catalog.Version,
            [.. catalog.PublishedTools.Select(t => new PublishedToolView(
                t.PublishedName, t.Service, t.Declaration.Description,
                t.Declaration.RequiredScope, t.Declaration.EgressClass))],
            [.. catalog.Drifts.Select(d => new ToolDriftView(d.Kind, d.Target, d.Detail))])));

        return app;
    }

    private static async Task<IResult> SetEnabledAsync(
        string clientId, bool enabled, McpDbContext db, TimeProvider clock)
    {
        var client = await db.Clients.FirstOrDefaultAsync(c => c.ClientId == clientId);
        if (client is null) return Results.NotFound();
        client.SetEnabled(enabled, clock.GetUtcNow());
        await db.SaveChangesAsync();
        return Results.Ok(ToView(client));
    }

    private static IResult Problem(string message)
        => Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [message] });

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

    private static string TierName(int tier) => (EgressTier)tier switch
    {
        EgressTier.SelfHosted => "self-hosted",
        EgressTier.ProtectedExternal => "protected-external",
        _ => "standard-external"
    };

    private static McpClientView ToView(McpClient c) => new(
        c.Id, c.ClientId, c.DisplayName,
        c.Kind == McpClientKind.ServiceAccount ? "service-account" : "interactive",
        c.Enabled, c.Attributes, TierName(c.EgressTier), c.RegisteredAt, c.UpdatedAt);
}
