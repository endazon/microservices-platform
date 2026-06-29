using KnowledgePlatform.Shared.Contracts.Dtos;
using System.Net.Http.Json;

namespace KnowledgePlatform.Bff.Endpoints;

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

        return app;
    }
}

public record AnalysisRequest(string Question, string? Scope = null);
