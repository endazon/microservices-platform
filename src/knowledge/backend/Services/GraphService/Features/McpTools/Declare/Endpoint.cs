namespace GraphService.Features.McpTools.Declare;

// FR-16, FR-15, ADR-0024 §2: ツール定義の自己申告（メッシュ内部限定）。
// FR-15 の `GET /internal/introspection` と同じ規約系・同じ防御に置く（ingress へは公開しない）。
public static class McpToolEndpoints
{
    // McpServer の `HttpToolDeclarationSource.ToolsPath` と同じパス。
    public const string ToolsPath = "/internal/mcp-tools";

    public static IEndpointRouteBuilder MapMcpToolEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ToolsPath, ()
                => Results.Ok(McpToolDeclarationSource.Declare()))
           .WithName("GraphServiceMcpTools")
           .ExcludeFromDescription();
        // FR-16, NFR-16, ADR-0029, ADR-0075, [[IADR-0379]], [[IADR-0462]]（2026-09-26 追記 / #1515, #1255 経路 ④-a）:
        // 🔴 **REST と gRPC の申告面を必ず対で張る。** 扇形の経路は宛先の側が面を持たないと 1 経路も移らず、
        // 張り忘れた宛先は MCP サーバーからは「申告なし」としか見えない（収集は失敗を申告なしへ畳む）。
        // 申告を張る唯一の口に gRPC 面を同居させ、張り忘れを構造で起こさない。面は ServiceCaller を要求する。
        app.MapGrpcService<McpToolDeclarationGrpcService>();
        // FR-16, ADR-0117 決定 1〜3（2026-09-27 追記 / #1611 段 3）: 🔴 **申告した口に実行口を対で張る。** 宛先は
        // 「申告したサービス＋ツール名」なので、申告を張るサービスは実行口も張る（張り忘れると、一覧に出るツールが
        // UNIMPLEMENTED で拒否され続ける）。面は ServiceCaller を要求し、本文の利用者文脈は MCP サーバー（許可集合）が運んだときだけ信じる。
        app.MapGrpcService<Execute.McpToolExecutionGrpcService>();
        return app;
    }
}
