using McpServer.Domain;

namespace McpServer.Features.McpClients.ListEffectiveTools;

// FR-16, SC-12「公開ツール一覧の確認」/ ADR-0024 §5: 実効ツール一覧（申告 ∩ 公開構成）と構成ドリフト。
public static class ListEffectiveToolsEndpoint
{
    public static IEndpointRouteBuilder MapListEffectiveTools(this IEndpointRouteBuilder app)
    {
        app.MapGet("/tools", (ToolCatalog catalog) => Results.Ok(new EffectiveToolsView(
            catalog.Version,
            [.. catalog.PublishedTools.Select(t => new PublishedToolView(
                t.PublishedName, t.Service, t.Declaration.Description,
                t.Declaration.RequiredScope, t.Declaration.EgressClass))],
            [.. catalog.Drifts.Select(d => new ToolDriftView(d.Kind, d.Target, d.Detail))])));

        return app;
    }
}
