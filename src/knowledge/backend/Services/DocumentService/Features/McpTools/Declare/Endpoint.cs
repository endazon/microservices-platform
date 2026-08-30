using DocumentService.Features.McpTools;

namespace DocumentService.Features.McpTools.Declare;

// FR-16, FR-15, ADR-0024 §2: ツール定義の自己申告（メッシュ内部限定）。
//
// FR-15 の `GET /internal/introspection` と**同じ規約系・同じ防御**に置く ——
// ネットワーク分離と mTLS が防御であり、ingress へは公開しない（`ExcludeFromDescription`）。
// ADR-0024 §決定「コア改修不要の追従」は、ツール公開の手順を
// 「(1) 自サービスに `/internal/mcp-tools` を実装 (2) 公開構成に追記」の 2 手順に保つと定める。
// 本ファイルが (1) である。
public static class McpToolEndpoints
{
    // McpServer の `HttpToolDeclarationSource.ToolsPath` と同じパス。
    public const string ToolsPath = "/internal/mcp-tools";

    public static IEndpointRouteBuilder MapMcpToolEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ToolsPath, (IConfiguration configuration)
                => Results.Ok(McpToolDeclarationSource.Declare(configuration)))
           .WithName("DocumentServiceMcpTools")
           .ExcludeFromDescription();
        return app;
    }
}
