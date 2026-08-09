using System.Text.Encodings.Web;
using System.Text.Json;
using AiAnalysisService.Api.Foundation.Services;
using Knowledge.Contracts.Dtos;

namespace AiAnalysisService.Api.Foundation.Endpoints;

// FR-04, FR-07, UC-01, UC-02: AI 分析・回答エンドポイント
public static class AnalysisEndpoints
{
    // IADR-0037: SSE data 行の JSON。camelCase・日本語を過剰エスケープしない緩和エンコーダ。
    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IEndpointRouteBuilder MapAnalysisEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/analysis").WithTags("Analysis");

        // FR-04, UC-01: RAG 質問回答
        g.MapPost("/ask", async (AskRequest req, IRagOrchestrator rag,
            HttpContext http) =>
        {
            // JWT から userId を取得（テスト環境では anonymous を使用）
            var userId = http.User.Identity?.Name ?? "anonymous";
            var userAttrs = ExtractUserAttributes(http);
            var answer = await rag.AskAsync(req.Question, userId, userAttrs, req.AttributeFilters);
            return Results.Ok(answer);
        }).WithName("Ask").Produces<AiAnswerDto>();

        // IADR-0037, FR-04, UC-01: RAG 質問回答の SSE ストリーミング。
        // event: citations（出典・先行）→ event: token（本文増分）* → event: done（回答ID/モデル/トークン）。
        g.MapPost("/ask/stream", async (AskRequest req, IRagOrchestrator rag,
            HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            var userId = http.User.Identity?.Name ?? "anonymous";
            var userAttrs = ExtractUserAttributes(http);

            await foreach (var ev in rag.AskStreamAsync(req.Question, userId, userAttrs, req.AttributeFilters, ct))
            {
                var (name, payload) = ev switch
                {
                    AskCitationsEvent c => ("citations", (object)new { citations = c.Citations }),
                    AskTokenEvent t => ("token", new { text = t.Text }),
                    AskDoneEvent d => ("done", new
                    {
                        answerId = d.AnswerId,
                        model = d.Model,
                        inputTokens = d.InputTokens,
                        outputTokens = d.OutputTokens,
                    }),
                    AskErrorEvent e => ("error", new { message = e.Message }),
                    _ => ("error", new { message = "unknown event" }),
                };
                await http.Response.WriteAsync(
                    $"event: {name}\ndata: {JsonSerializer.Serialize(payload, SseJson)}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }
        }).WithName("AskStream");

        // FR-07, UC-02: 指定データ範囲での分析・比較・抽出
        g.MapPost("/analyze", async (AnalysisTaskRequest req, IRagOrchestrator rag,
            HttpContext http) =>
        {
            // FR-07: 指示は必須。空依頼は受け付けない（バリデーション）。
            if (string.IsNullOrWhiteSpace(req.Instruction))
                return Results.BadRequest(new { error = "instruction is required" });

            // FR-07: プロンプトインジェクション緩和。過大な指示は受け付けない。
            if (req.Instruction.Length > AnalysisPromptBuilder.MaxInstructionLength)
                return Results.BadRequest(new
                {
                    error = $"instruction must be {AnalysisPromptBuilder.MaxInstructionLength} characters or fewer"
                });

            // FR-05: JWT から利用者を特定し、権限解決（範囲は権限を広げない）
            var userId = http.User.Identity?.Name ?? "anonymous";
            var userAttrs = ExtractUserAttributes(http);
            var answer = await rag.AnalyzeAsync(req, userId, userAttrs);
            return Results.Ok(answer);
        }).WithName("Analyze").Produces<AiAnswerDto>();

        return app;
    }

    private static Dictionary<string, string> ExtractUserAttributes(HttpContext ctx)
    {
        var attrs = new Dictionary<string, string>();
        var clearance = ctx.User.FindFirst("clearance")?.Value;
        var department = ctx.User.FindFirst("department")?.Value;
        if (clearance is not null) attrs["clearance"] = clearance;
        if (department is not null) attrs["department"] = department;
        return attrs;
    }
}

// FR-04, FR-05, SC-01, SC-08, #539: 対象範囲（属性フィルタ）。
//
// **`SearchRequest` は既に `AttributeFilters` を持つのに、こちらだけ持たなかった**
// （計画 L198・裁定 Q1 が「非対称を解消する」と定めた）。
//
// **多値である**（キー → 許可値の集合）。画面はチップを**複数**選ぶので単値では表現できない。
// `SearchRequest` 側の単値は自ら「後方互換」と名乗っている形であり、手本にしない。
//
// **範囲は narrowing-only である** —— ABAC 許可スコープと交差させ、**権限を一切広げない**
// （`DataRangeScopeResolver`）。クライアントが権限外の値を送っても結果は広がらない。
//
// 既定値つきで足すので**契約上は非破壊**である（[[IADR-0122]] 決定 2）。
public record AskRequest(
    string Question,
    string? Scope = null,
    Dictionary<string, List<string>>? AttributeFilters = null);
