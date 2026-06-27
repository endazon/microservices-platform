using KnowledgePlatform.Shared.Contracts.Dtos;
using System.Net.Http.Json;

namespace AiAnalysisService.Api.Services;

// FR-04, FR-07, UC-01, UC-02: RAG 回答生成オーケストレーター
// フロー: ABAC スコープ解決 → ハイブリッド検索 → LLM 回答生成
public class RagOrchestrator(
    IHttpClientFactory httpFactory,
    IConfiguration config) : IRagOrchestrator
{
    public async Task<AiAnswerDto> AskAsync(string question, string userId,
        Dictionary<string, string> userAttributes, CancellationToken ct = default)
    {
        // FR-05: ABAC 権限スコープ解決。解決に失敗した場合も deny-by-default（Granted=false）とする。
        var authzClient = httpFactory.CreateClient("AuthorizationService");
        var scopeResp = await authzClient.PostAsJsonAsync("/authz/scope",
            new AccessScopeRequest(userId, userAttributes), ct);
        var scope = (scopeResp.IsSuccessStatusCode
            ? await scopeResp.Content.ReadFromJsonAsync<AccessScopeResponse>(ct)
            : null) ?? new AccessScopeResponse(userId, [], false);

        // FR-05: 閲覧可能な文書が無い（許可ポリシー無し）場合は、検索・LLM を呼ばず空回答へ縮退。
        // 多値 allow-list を一切破棄せず後段へ伝播する（機密属性の漏れを防ぐ）。
        var model = config["Llm:DefaultModel"] ?? "claude-sonnet-4-6";
        if (!scope.Granted)
            return new AiAnswerDto(
                "閲覧権限のある文書が見つかりませんでした。", [], model, 0, 0);

        // FR-05: ABAC アクセススコープ（多値 allow-list）を検索へ伝播
        var accessScope = new AccessScope(scope.AllowedFilters, scope.Granted);

        var retrievalClient = httpFactory.CreateClient("RetrievalService");
        var searchResp = await retrievalClient.PostAsJsonAsync("/search",
            new SearchRequest(question, 5, null, accessScope), ct);
        var searchResult = searchResp.IsSuccessStatusCode
            ? await searchResp.Content.ReadFromJsonAsync<SearchResponse>(ct)
            : new SearchResponse([], 0, 0);

        // FR-04: 検索結果を番号付き出典へ写像（回答本文の [1][2] と一致させる）
        var citations = CitationMapper.ToCitations(searchResult?.Results ?? []);

        // FR-04: 関連チャンクを文脈にして LLM ゲートウェイで回答生成
        var context = CitationMapper.BuildContext(citations);
        var prompt = BuildPrompt(question, context);

        var llmClient = httpFactory.CreateClient("LlmGateway");
        var completionResp = await llmClient.PostAsJsonAsync("/complete",
            new { prompt, maxTokens = 1024, model = config["Llm:DefaultModel"] ?? "claude-sonnet-4-6" }, ct);

        string answer;
        int inputTokens = 0, outputTokens = 0;

        if (completionResp.IsSuccessStatusCode)
        {
            var completion = await completionResp.Content
                .ReadFromJsonAsync<CompletionApiResponse>(ct);
            answer = completion?.Text ?? "回答を生成できませんでした。";
            inputTokens = completion?.InputTokens ?? 0;
            outputTokens = completion?.OutputTokens ?? 0;
            model = completion?.Model ?? model;
        }
        else
        {
            // FR-04 縮退: LLM 不調時は検索結果のみ返す
            answer = searchResult?.Results.Count > 0
                ? "LLM が現在利用できないため、関連文書の一覧を返します。"
                : "関連する情報が見つかりませんでした。";
        }

        return new AiAnswerDto(answer, citations, model, inputTokens, outputTokens);
    }

    private static string BuildPrompt(string question, string context)
        => $"""
            以下の参照文書を根拠に、質問に答えてください。根拠のない情報は含めないでください。

            ## 参照文書
            {context}

            ## 質問
            {question}

            ## 回答（日本語で、出典番号 [1][2] を付けてください）
            """;

    private record CompletionApiResponse(string Text, string Model, int InputTokens, int OutputTokens);
}
