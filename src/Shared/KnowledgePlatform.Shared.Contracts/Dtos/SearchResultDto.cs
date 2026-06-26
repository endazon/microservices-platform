namespace KnowledgePlatform.Shared.Contracts.Dtos;

// FR-03, FR-04: 検索結果の1件（チャンク単位）
public record SearchResultDto(
    Guid ChunkId,
    Guid DocumentId,
    string DocumentTitle,
    string Text,
    float Score,
    string? MarkdownUri,
    Dictionary<string, string> Attributes,
    List<string> Tags);

// FR-04: RAG 回答レスポンス
public record AiAnswerDto(
    string Answer,
    List<SearchResultDto> Citations,
    string Model,
    int InputTokens,
    int OutputTokens);
