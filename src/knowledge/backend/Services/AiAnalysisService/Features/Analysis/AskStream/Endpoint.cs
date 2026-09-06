using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using AiAnalysisService.Common.Observability;
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
            RagStreamMetrics metrics, HttpContext http, CancellationToken ct) =>
        {
            // NFR-02, ADR-0076 決定 5, IADR-0354 (#1204): TTFT の**起点**。
            // ミドルウェア（相関 ID・認証）を通過した後のハンドラ入口である。その差分は本計器に
            // 含まれないが、同じ経路を丸ごと測る http_server_request_duration_seconds との差で観測できる。
            var startedAt = Stopwatch.GetTimestamp();

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            // 🔴 FR-05, [[IADR-0335]] 決定 4 (#1318): **未認証は認可サービスを呼ばずに倒す。**
            // 判定と理由は `AnalysisEndpoints.IsAnonymous` に 1 つだけ置く。
            //
            // **200 ＋ SSE のまま**である（401 にしない）。ヘッダは既に書いており、
            // 本文は `RagOrchestrator` が `Granted=false` のときに流すのと**同じ 3 イベント**
            // （`NoAccessAnswer.StreamEvents()`）である ——「拒否」と「見えるものが無い」を
            // 区別させない（存在秘匿・[[IADR-0009]]）。
            //
            // 🔴 **TTFT は記録しない。** 検索も生成も行っていないので、ここで計器を進めると
            // 「初回トークンが速かった」として p95 を下振れさせる（[[IADR-0354]] と同じ理由）。
            if (AnalysisEndpoints.IsAnonymous(http))
            {
                foreach (var denied in NoAccessAnswer.StreamEvents())
                    await WriteEventAsync(http, denied, ct);
                return;
            }

            // **ここへ到達するのは認証済みの要求だけである。**
            var userId = http.User.Identity!.Name ?? "anonymous";
            var userAttrs = AnalysisEndpoints.ExtractUserAttributes(http);
            var firstTokenRecorded = false;

            await foreach (var ev in rag.AskStreamAsync(req.Question, userId, userAttrs, req.AttributeFilters, ct))
            {
                await WriteEventAsync(http, ev, ct);

                // NFR-02, ADR-0076 決定 5, IADR-0354 (#1204): TTFT の**終点**は
                // **最初の token フレームを書き、フラッシュし終えた時刻**である（バイトがサーバを出た瞬間）。
                // 🔴 **citations では止めない。** 出典は本文のトークンではなく LLM 生成の前に確定するため、
                //    ここで止めると「生成が始まる前の時刻」を初回応答として記録することになる。
                // 🔴 **token が 1 件も出なければ記録しない**（error のみ・途中終端・取り消し）。
                //    0 を積むと「初回トークンが無かった」が「速かった」として p95 を下振れさせる。
                if (!firstTokenRecorded && ev is AskTokenEvent)
                {
                    firstTokenRecorded = true;
                    metrics.RecordFirstToken(
                        Stopwatch.GetElapsedTime(startedAt), RagStreamMetrics.PurposeRagAnswer);
                }
            }
        }).WithName("AskStream");
    }

    // IADR-0037: 1 イベントを SSE フレームへ写して書き、フラッシュする。
    // 🔴 **書き出しの形はここだけが持つ**（#1318 で未認証の縮退も同じ形を流すようになったため、
    // 2 箇所へ分かれると `event:` / `data:` の綴りが割れる余地ができる）。
    private static async Task WriteEventAsync(HttpContext http, AskEvent ev, CancellationToken ct)
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
}
