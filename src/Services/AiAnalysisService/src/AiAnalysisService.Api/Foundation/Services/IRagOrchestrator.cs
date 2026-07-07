using KnowledgePlatform.Shared.Contracts.Dtos;

namespace AiAnalysisService.Api.Foundation.Services;

// FR-04, FR-07: RAG オーケストレーションポート
public interface IRagOrchestrator
{
    // FR-04, UC-01: 自然文の質問に対し、権限内文書を根拠に回答＋出典を返す。
    Task<AiAnswerDto> AskAsync(string question, string userId,
        Dictionary<string, string> userAttributes, CancellationToken ct = default);

    // FR-07, UC-02: 指定データ範囲（range）に対する分析・比較・抽出を行い、回答＋出典を返す。
    // データ範囲は ABAC 許可スコープと交差し、権限を広げない（narrowing-only）。
    Task<AiAnswerDto> AnalyzeAsync(AnalysisTaskRequest request, string userId,
        Dictionary<string, string> userAttributes, CancellationToken ct = default);
}
