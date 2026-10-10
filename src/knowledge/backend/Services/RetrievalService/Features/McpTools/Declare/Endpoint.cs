namespace RetrievalService.Features.McpTools.Declare;

// FR-16, FR-15, ADR-0024 §2: ツール定義の自己申告（メッシュ内部限定）。
// FR-15 の `GET /internal/introspection` と同じ規約系・同じ防御に置く（ingress へは公開しない）。
public static class McpToolEndpoints
{
    public static IEndpointRouteBuilder MapMcpToolEndpoints(this IEndpointRouteBuilder app)
    {
        // FR-16, NFR-16, ADR-0029, ADR-0075, [[IADR-0379]], [[IADR-0462]]（2026-09-26 追記 / #1515, #1255 経路 ④-a）:
        // 申告の面は gRPC だけである（張り忘れた宛先は MCP サーバーからは「申告なし」としか見えない）。面は ServiceCaller を要求する。
        // ［2026-10-10 / #1517・計画 ADR-0089 決定 1・[[IADR-0533]] 決定 3］REST の `GET /internal/mcp-tools` は撤去した
        // （従前は REST と gRPC の申告面を対で張っていた。収集側が gRPC だけで集めるようになり、呼び出し元が 0 になった）。
        app.MapGrpcService<McpToolDeclarationGrpcService>();
        // FR-16, ADR-0117 決定 1〜3 (#1611): 🔴 **申告した口に実行口を対で張る。** 宛先は「申告したサービス＋ツール名」なので、
        // 申告を張るサービスは実行口も張る（張り忘れると、一覧に出るツールが UNIMPLEMENTED で拒否され続ける）。
        // 面は ServiceCaller を要求し、本文の利用者文脈は MCP サーバー（許可集合）が運んだときだけ信じる。
        app.MapGrpcService<Execute.McpToolExecutionGrpcService>();
        return app;
    }
}
