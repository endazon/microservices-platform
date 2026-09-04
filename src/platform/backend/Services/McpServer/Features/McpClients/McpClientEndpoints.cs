using McpServer.Domain;
using McpServer.Domain.Ports;
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
        return Results.Ok(McpClientMapper.ToView(client));
    }

    // 🔴 FR-16, UC-09, SC-12, ADR-0034 決定 9, ADR-0062 決定 2・3:
    // **無人アカウントへの属性割当の統制は、登録と差し替えが同じ 1 つの関数を呼ぶ。**
    // 2 か所へ書くと片方だけが緩む（既存の `ValidateServiceAccountAttributes` の作法と同じ理由。
    // 計画 ADR-0062 §決定 3 も「登録だけ塞いで差し替えが緩い形」を許さない）。
    //
    // 妥当なら null、拒否するなら 400 ValidationProblem を返す。
    // **拒否理由は丸めない** —— どの値が外れたかを本文へ載せる（ADR-0062 §結果。画面が事前に
    // 示せないことの唯一の緩和策である）。
    internal static async Task<IResult?> RejectUnassignableAsync(
        string clientId,
        McpClientKind kind,
        IReadOnlyDictionary<string, string> attributes,
        IRegistrarAttributeResolver registrar,
        CancellationToken ct)
    {
        // 有人は利用者本人の属性で解決される。割り当てる属性が無いので統制の対象でもない。
        if (kind != McpClientKind.ServiceAccount) return null;

        // ADR-0024（2026-08-02 注記）/ ADR-0034 決定 9: 個人資料を読ませる属性割当の禁止。
        // **構成（公開構成のスキーマ検証）と API の両方で弾く。検証関数は 1 つを共用する。**
        var forbidden = ToolPublicationConfigValidator.ValidateServiceAccountAttributes(clientId, attributes);
        if (forbidden.Count > 0) return Problem(forbidden);

        // ADR-0062 決定 2: `clearance` / タグは登録者が持つ集合の部分集合であること。
        // 🔴 **対象の属性を 1 つも含まないなら登録者の解決を呼ばない** —— 呼ぶと、属性を持たない
        // 無人アカウントの登録まで認可サービスの可用性に従属する。
        if (!ServiceAccountAttributeSubset.Governs(attributes)) return null;

        var assignable = await registrar.ResolveAsync(ct);
        var outside = ServiceAccountAttributeSubset.Validate(clientId, attributes, assignable);
        return outside.Count > 0 ? Problem(outside) : null;
    }

    internal static IResult Problem(string message) => Problem([message]);

    // **理由をすべて返す**（先頭 1 件へ丸めない）。画面は `ApiError.details` をそのまま並べて出す。
    internal static IResult Problem(IReadOnlyList<string> messages)
        => Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [.. messages] });

    // 手書きの詰め替え（ToView）とティア名の変換（TierName）は撤去した。写像は
    // `McpClientMapper.ToView`（Riok.Mapperly の生成マッパ）が持つ
    // （計画 ADR-0030 §決定 / IADR-0371 決定 3 / IADR-0376）。**このクラスに写像は残さない。**
}
