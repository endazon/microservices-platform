using Knowledge.Bff.Endpoints.Usage;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Net.Http;
using System.Net.Http.Json;

namespace Knowledge.Bff.Endpoints;

// FR-04, FR-07, UC-01, UC-02: BFF AI 分析集約エンドポイント
public static class AnalysisBffEndpoints
{
    public static IEndpointRouteBuilder MapAnalysisBffEndpoints(this IEndpointRouteBuilder app)
    {
        // NFR-09, #656: **認証を要求する。** 計画の暫定運用は「エッジ（BFF）で OIDC/JWT を担保する」と
        // 定めており、本群はそれを満たしていなかった（無認証で到達できた）。
        // **ロールは要求しない**（SC-01 / SC-08 は利用者グループ。計画 `05_screens`）。
        // 無認証でも `RagOrchestrator` が `!Granted` で LLM 呼び出し前に縮退していたが、
        // **その安全は ABAC ポリシーの内容だけに依存していた**（IADR-0044 の多層防御）。
        var g = app.MapGroup("/bff/analysis").WithTags("Analysis BFF").RequireAuthorization();

        // FR-04, UC-01, UC-02: 検索結果を根拠に AI 回答＋出典を返す。
        // AiAnalysisService へ集約し、ABAC 権限解決のため Authorization ヘッダを伝播する。
        g.MapPost("/ask", async (
            AnalysisRequest req,
            IHttpClientFactory httpFactory,
            IUsageEventReporter usage,
            HttpContext http,
            CancellationToken ct) =>
        {
            var client = httpFactory.CreateClient("AiAnalysisService");

            // FR-05: 権限の無い文書を結果に出さないため、利用者の資格情報を後段へ引き継ぐ
            var auth = http.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrEmpty(auth))
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);

            var resp = await client.PostAsJsonAsync("/analysis/ask", req, ct);
            if (!resp.IsSuccessStatusCode)
                return Results.StatusCode((int)resp.StatusCode);

            var answer = await resp.Content.ReadFromJsonAsync<AiAnswerDto>(ct);
            if (answer is null) return Results.StatusCode(StatusCodes.Status502BadGateway);

            // FR-10, SC-10, [[IADR-0336]] (#1103): **利用状況イベント（answer）を発火する。**
            // 回答が実際に生成できたときだけ数える（後段の非 2xx・本文 null は上で返っている）。
            // 🔴 **質問文は送らない** —— 受け口は `answer` の `query` を捨てるので、捨てられる
            // 自由文を経路とログに晒す理由が無い（決定 5）。
            ReportAnswer(usage, http);
            return Results.Ok(answer);
        }).WithName("BffAnalysisAsk").Produces<AiAnswerDto>();

        // FR-07, UC-02: 指定データ範囲での分析・比較・抽出を AiAnalysisService へ集約する。
        // ABAC 権限解決のため Authorization ヘッダを後段へ伝播する（権限外文書を出さない）。
        g.MapPost("/analyze", async (
            AnalysisTaskRequest req,
            IHttpClientFactory httpFactory,
            IUsageEventReporter usage,
            HttpContext http,
            CancellationToken ct) =>
        {
            var client = httpFactory.CreateClient("AiAnalysisService");

            // FR-05: 権限の無い文書を結果に出さないため、利用者の資格情報を後段へ引き継ぐ
            var auth = http.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrEmpty(auth))
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);

            var resp = await client.PostAsJsonAsync("/analysis/analyze", req, ct);
            if (!resp.IsSuccessStatusCode)
                return Results.StatusCode((int)resp.StatusCode);

            var answer = await resp.Content.ReadFromJsonAsync<AiAnswerDto>(ct);
            if (answer is null) return Results.StatusCode(StatusCodes.Status502BadGateway);

            // FR-10, SC-10, [[IADR-0336]] (#1103): **分析も `answer` として数える。**
            // 契約の `answer` は「AI 回答生成」であって SC-01 の質問に限られていない ——
            // 本経路も LLM が根拠つきの `AiAnswerDto` を生成する。落とすと総回答数が
            // 実際の生成回数と食い違う（作業仕様書 §母集合 4）。
            ReportAnswer(usage, http);
            return Results.Ok(answer);
        }).WithName("BffAnalysisAnalyze").Produces<AiAnswerDto>();

        // IADR-0037, FR-04, UC-01, SC-01: RAG 回答の SSE ストリーミングを AiAnalysisService へパススルーする。
        // ABAC 権限解決のため Authorization を伝播し、上流 SSE をそのままクライアントへ中継する（逐次フラッシュ）。
        g.MapPost("/ask/stream", async (
            AnalysisRequest req,
            IHttpClientFactory httpFactory,
            IUsageEventReporter usage,
            HttpContext http,
            CancellationToken ct) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            var client = httpFactory.CreateClient("AiAnalysisService");
            using var upReq = new HttpRequestMessage(HttpMethod.Post, "/analysis/ask/stream")
            {
                Content = JsonContent.Create(req),
            };
            var auth = http.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrEmpty(auth))
                upReq.Headers.TryAddWithoutValidation("Authorization", auth);

            HttpResponseMessage? upResp = null;
            try
            {
                upResp = await client.SendAsync(upReq, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // 上流不達は SSE の error イベントで中立に伝える（HTTP ステータスは変えられない＝既に 200）。
                await http.Response.WriteAsync("event: error\ndata: {\"message\":\"分析に失敗しました。\"}\n\n", ct);
                return;
            }

            using (upResp)
            {
                if (!upResp.IsSuccessStatusCode)
                {
                    await http.Response.WriteAsync("event: error\ndata: {\"message\":\"分析に失敗しました。\"}\n\n", ct);
                    return;
                }

                // FR-10, SC-10, [[IADR-0336]] (#1103): **上流が 2xx を返した時点で `answer` を数える。**
                // SSE は 200 のヘッダを先に返すため「回答が生成された」と言えるのはここである
                // （中継の途中で切れたか最後まで届いたかは利用者側の事情であり、回答の生成回数は変わらない）。
                ReportAnswer(usage, http);

                await using var upstream = await upResp.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[4096];
                int read;
                // 逐次フラッシュで中継し、真のストリーミング（低遅延）を保つ。
                while ((read = await upstream.ReadAsync(buffer, ct)) > 0)
                {
                    await http.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                    await http.Response.Body.FlushAsync(ct);
                }
            }
        }).WithName("BffAnalysisAskStream");

        return app;
    }

    // FR-10, SC-10, [[IADR-0336]] 決定 5 (#1103): 回答生成 3 経路が共有する発火。
    //
    // 🔴 **`query` を渡さない。** 受け口は種別が `answer` のとき検索語を保持しないので、
    // 質問文（利用者の自由文）を送っても捨てられるだけである。捨てられる自由文を
    // ネットワークと相手側のログに晒さない（ADR-0006 §結果）。
    //
    // 🔴 **応答を待たない。** `Report` は列へ載せるだけで例外も投げないため、
    // 計測の失敗で回答が失敗することはない（fail-open）。
    private static void ReportAnswer(IUsageEventReporter usage, HttpContext http)
        => usage.Report(new UsageEventSignal(
            UsageEventType.Answer, null, http.Request.Headers.Authorization.ToString()));
}

// FR-04, FR-05, SC-01, SC-08, #539: 対象範囲（属性フィルタ）。**後段の `AskRequest` と同じ形である。**
//
// **BFF だけに足しても意味が無い** —— ここはパススルーであり、絞り込みを実効するのは
// AiAnalysisService 側だからである。**両方を同時に足す。**
//
// **ABAC スコープはクライアントから受け取らない**（受け取っても使わない＝権限昇格の防止）。
// ここで受けるのは「利用者が自分の権限の内側をさらに絞る」指定だけである。
public record AnalysisRequest(
    string Question,
    string? Scope = null,
    Dictionary<string, List<string>>? AttributeFilters = null);
