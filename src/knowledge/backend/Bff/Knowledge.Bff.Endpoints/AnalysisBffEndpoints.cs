using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Shared.Contracts.Dtos;
using System.Net.Http;
using System.Net.Http.Json;

namespace Knowledge.Bff.Endpoints;

// FR-04, FR-07, UC-01, UC-02: BFF AI 分析集約エンドポイント
public static class AnalysisBffEndpoints
{
    public static IEndpointRouteBuilder MapAnalysisBffEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/bff/analysis").WithTags("Analysis BFF");

        // FR-04, UC-01, UC-02: 検索結果を根拠に AI 回答＋出典を返す。
        // AiAnalysisService へ集約し、ABAC 権限解決のため Authorization ヘッダを伝播する。
        g.MapPost("/ask", async (
            AnalysisRequest req,
            IHttpClientFactory httpFactory,
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
            return answer is null ? Results.StatusCode(StatusCodes.Status502BadGateway) : Results.Ok(answer);
        }).WithName("BffAnalysisAsk").Produces<AiAnswerDto>();

        // FR-07, UC-02: 指定データ範囲での分析・比較・抽出を AiAnalysisService へ集約する。
        // ABAC 権限解決のため Authorization ヘッダを後段へ伝播する（権限外文書を出さない）。
        g.MapPost("/analyze", async (
            AnalysisTaskRequest req,
            IHttpClientFactory httpFactory,
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
            return answer is null ? Results.StatusCode(StatusCodes.Status502BadGateway) : Results.Ok(answer);
        }).WithName("BffAnalysisAnalyze").Produces<AiAnswerDto>();

        // IADR-0037, FR-04, UC-01, SC-01: RAG 回答の SSE ストリーミングを AiAnalysisService へパススルーする。
        // ABAC 権限解決のため Authorization を伝播し、上流 SSE をそのままクライアントへ中継する（逐次フラッシュ）。
        g.MapPost("/ask/stream", async (
            AnalysisRequest req,
            IHttpClientFactory httpFactory,
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
}

public record AnalysisRequest(string Question, string? Scope = null);
