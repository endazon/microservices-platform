using KnowledgePlatform.Shared.Contracts.Dtos;

namespace AiAnalysisService.Api.Services;

// FR-04, FR-07: RAG オーケストレーションポート
public interface IRagOrchestrator
{
    Task<AiAnswerDto> AskAsync(string question, string userId,
        Dictionary<string, string> userAttributes, CancellationToken ct = default);
}
