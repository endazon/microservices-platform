using McpServer.Domain;
using McpServer.Features.McpClients.DisableClient;
using McpServer.Features.McpClients.EnableClient;
using McpServer.Features.McpClients.ListClients;
using McpServer.Features.McpClients.ListEffectiveTools;
using McpServer.Features.McpClients.RegisterClient;
using McpServer.Features.McpClients.ReplaceAttributes;
using McpServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace McpServer.Features.McpClients;

// FR-16, UC-09, SC-12: MCP クライアント登録管理スライスの合成点。**管理者限定**（SC-12「管理者限定」）。
//
// ADR-0065 決定 2: 1 ユースケースのファイルは操作フォルダへ束ねる。
// **本ファイルに残すのは、グループ（パス・タグ・認可）の構築と複数操作が共有するヘルパだけである。**
public static class McpClientEndpoints
{
    public static IEndpointRouteBuilder MapMcpClientEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/mcp-clients").WithTags("McpClients")
            .RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        g.MapListMcpClients();
        g.MapRegisterMcpClient();
        g.MapDisableMcpClient();
        g.MapEnableMcpClient();
        g.MapReplaceMcpClientAttributes();
        g.MapListEffectiveTools();

        return app;
    }

    // UC-09 / SC-12: 無効化と再有効化は**同じ 1 つのハンドラ**である（引数の真偽だけが違う）。
    // 操作フォルダごとに複製すると、片方だけ直したときに黙ってズレる。集約直下に 1 つ置く。
    internal static async Task<IResult> SetEnabledAsync(
        string clientId, bool enabled, McpDbContext db, TimeProvider clock)
    {
        var client = await db.Clients.FirstOrDefaultAsync(c => c.ClientId == clientId);
        if (client is null) return Results.NotFound();
        client.SetEnabled(enabled, clock.GetUtcNow());
        await db.SaveChangesAsync();
        return Results.Ok(ToView(client));
    }

    internal static IResult Problem(string message)
        => Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [message] });

    internal static string TierName(int tier) => (EgressTier)tier switch
    {
        EgressTier.SelfHosted => "self-hosted",
        EgressTier.ProtectedExternal => "protected-external",
        _ => "standard-external"
    };

    internal static McpClientView ToView(McpClient c) => new(
        c.Id, c.ClientId, c.DisplayName,
        c.Kind == McpClientKind.ServiceAccount ? "service-account" : "interactive",
        c.Enabled, c.Attributes, TierName(c.EgressTier), c.RegisteredAt, c.UpdatedAt);
}
