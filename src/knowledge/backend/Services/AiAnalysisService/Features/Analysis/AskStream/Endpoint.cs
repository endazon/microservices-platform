using System.Text.Encodings.Web;
using System.Text.Json;
using AiAnalysisService.Domain;
using AiAnalysisService.Domain.Ports;

namespace AiAnalysisService.Features.Analysis.AskStream;

// IADR-0037, FR-04, UC-01: RAG 質問回答の SSE ストリーミング。
// event: citations（出典・先行）→ event: token（本文増分）* → event: done（回答ID/モデル/トークン）。
internal static class AskStreamEndpoint
{
    // IADR-0037: SSE data 行の JSON。camelCase・日本語を過剰エスケープしない緩和エンコーダ。
    // **この操作だけが使う**ため 3 段目に置く（ADR-0068 決定 2）。
    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/ask/stream", async (AskRequest req, IRagOrchestrator rag,
            HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            var userId = http.User.Identity?.Name ?? "anonymous";
            var userAttrs = AnalysisEndpoints.ExtractUserAttributes(http);

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
    }
}
