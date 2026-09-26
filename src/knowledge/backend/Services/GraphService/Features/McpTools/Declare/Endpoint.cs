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
        // FR-16, NFR-16, ADR-0029, ADR-0075, [[IADR-0379]], [[IADR-0462]]（2026-09-26 追記 / #1515, #1255 経路 ④-a）:
        // 🔴 **REST と gRPC の申告面を必ず対で張る。** 扇形の経路は宛先の側が面を持たないと 1 経路も移らず、
        // 張り忘れた宛先は MCP サーバーからは「申告なし」としか見えない（収集は失敗を申告なしへ畳む）。
        // 申告を張る唯一の口に gRPC 面を同居させ、張り忘れを構造で起こさない。面は ServiceCaller を要求する。
        app.MapGrpcService<McpToolDeclarationGrpcService>();
        return app;
    }
}
