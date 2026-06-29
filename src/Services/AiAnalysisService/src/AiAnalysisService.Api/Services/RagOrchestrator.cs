using KnowledgePlatform.Shared.Contracts.Dtos;
using System.Net.Http.Json;

namespace AiAnalysisService.Api.Services;

// FR-04, FR-07, UC-01, UC-02: RAG 回答生成オーケストレーター
// フロー: ABAC スコープ解決 → （FR-07: データ範囲と交差）→ ハイブリッド検索 → LLM 回答生成
public class RagOrchestrator(
    IHttpClientFactory httpFactory,
    IConfiguration config) : IRagOrchestrator
{
    // FR-04: 質問回答で文脈に取り込む既定チャンク数。
    private const int DefaultAskTopK = 5;

    // FR-04, UC-01: 自然文質問に対する RAG 回答。
    public async Task<AiAnswerDto> AskAsync(string question, string userId,
        Dictionary<string, string> userAttributes, CancellationToken ct = default)
    {
        // FR-05: ABAC 権限スコープ解決。閲覧可能文書が無ければ空回答へ縮退（多値 allow-list を破棄しない）。
        var resolved = await ResolveScopeAsync(userId, userAttributes, ct);
        if (!resolved.Granted)
            return EmptyAnswer();

        var scope = new AccessScope(resolved.AllowedFilters, resolved.Granted);
        return await GenerateAsync(question, scope, DefaultAskTopK,
            context => BuildAskPrompt(question, context), ct);
    }

    // FR-07, UC-02: 指定データ範囲での分析・比較・抽出。
    public async Task<AiAnswerDto> AnalyzeAsync(AnalysisTaskRequest request, string userId,
        Dictionary<string, string> userAttributes, CancellationToken ct = default)
    {
        // FR-05: ABAC 権限スコープ解決。
        var resolved = await ResolveScopeAsync(userId, userAttributes, ct);

        // FR-07/FR-05: データ範囲を ABAC スコープと交差させ、権限を広げない実効スコープを得る。
        var scope = DataRangeScopeResolver.Resolve(resolved, request.Range);
        if (!scope.GrantsAccess)
            return EmptyAnswer();

        // 範囲の検索クエリが未指定なら指示文を流用して関連箇所を絞る。
        var query = string.IsNullOrWhiteSpace(request.Range?.Query)
            ? request.Instruction
            : request.Range!.Query!;
        var topK = request.Range?.TopK is > 0 ? request.Range.TopK : DefaultAskTopK;

        return await GenerateAsync(query, scope, topK,
            context => AnalysisPromptBuilder.Build(request, context), ct);
    }

    // FR-05: ABAC スコープ解決。解決失敗時も deny-by-default（Granted=false）へ縮退する。
    private async Task<AccessScopeResponse> ResolveScopeAsync(string userId,
        Dictionary<string, string> userAttributes, CancellationToken ct)
    {
        var authzClient = httpFactory.CreateClient("AuthorizationService");
        try
        {
            var scopeResp = await authzClient.PostAsJsonAsync("/authz/scope",
                new AccessScopeRequest(userId, userAttributes), ct);
            return (scopeResp.IsSuccessStatusCode
                ? await scopeResp.Content.ReadFromJsonAsync<AccessScopeResponse>(ct)
                : null) ?? new AccessScopeResponse(userId, [], false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // 認可サービスへの通信失敗（ネットワーク障害・タイムアウト）も deny-by-default へ縮退し、
            // 500 を伝播させない。呼び出し側のキャンセル要求は通常どおり伝播させる。
            return new AccessScopeResponse(userId, [], false);
        }
    }

    // FR-04, FR-07: 実効スコープで検索 → 番号付き出典へ写像 → LLM で本文生成、の共通パイプライン。
    private async Task<AiAnswerDto> GenerateAsync(string query, AccessScope scope, int topK,
        Func<string, string> buildPrompt, CancellationToken ct)
    {
        var model = config["Llm:DefaultModel"] ?? "claude-sonnet-4-6";

        var retrievalClient = httpFactory.CreateClient("RetrievalService");
        var searchResp = await retrievalClient.PostAsJsonAsync("/search",
            new SearchRequest(query, topK, null, scope), ct);
        var searchResult = searchResp.IsSuccessStatusCode
            ? await searchResp.Content.ReadFromJsonAsync<SearchResponse>(ct)
            : new SearchResponse([], 0, 0);

        // FR-04: 検索結果を番号付き出典へ写像（回答本文の [1][2] と一致させる）
        var citations = CitationMapper.ToCitations(searchResult?.Results ?? []);

        // FR-04: 関連チャンクを文脈にして LLM ゲートウェイで本文生成
        var context = CitationMapper.BuildContext(citations);
        var prompt = buildPrompt(context);

        var llmClient = httpFactory.CreateClient("LlmGateway");
        var completionResp = await llmClient.PostAsJsonAsync("/complete",
            new { prompt, maxTokens = 1024, model }, ct);

        if (completionResp.IsSuccessStatusCode)
        {
            var completion = await completionResp.Content
                .ReadFromJsonAsync<CompletionApiResponse>(ct);
            return new AiAnswerDto(
                completion?.Text ?? "回答を生成できませんでした。",
                citations,
                completion?.Model ?? model,
                completion?.InputTokens ?? 0,
                completion?.OutputTokens ?? 0);
        }

        // FR-04/FR-07 縮退: LLM 不調時は検索結果（出典）のみ返す
        var fallback = citations.Count > 0
            ? "LLM が現在利用できないため、関連文書の一覧を返します。"
            : "関連する情報が見つかりませんでした。";
        return new AiAnswerDto(fallback, citations, model, 0, 0);
    }

    // FR-05: 閲覧可能文書が無い場合の空回答（検索・LLM を呼ばずコストを抑える縮退）。
    private AiAnswerDto EmptyAnswer()
        => new(
            "閲覧権限のある文書が見つかりませんでした。",
            [],
            config["Llm:DefaultModel"] ?? "claude-sonnet-4-6",
            0, 0);

    private static string BuildAskPrompt(string question, string context)
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
