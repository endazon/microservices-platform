using GraphService.Features.McpTools;

namespace GraphService.Features.McpTools.Declare;

// FR-16, FR-15, ADR-0024 §2: ツール定義の自己申告（メッシュ内部限定）。
// FR-15 の `GET /internal/introspection` と同じ規約系・同じ防御に置く（ingress へは公開しない）。
public static class McpToolEndpoints
{
    // McpServer の `HttpToolDeclarationSource.ToolsPath` と同じパス。
    public const string ToolsPath = "/internal/mcp-tools";

    public static IEndpointRouteBuilder MapMcpToolEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ToolsPath, (IConfiguration configuration)
                => Results.Ok(McpToolDeclarationSource.Declare(configuration)))
           .WithName("GraphServiceMcpTools")
           .ExcludeFromDescription();
        return app;
    }
}
