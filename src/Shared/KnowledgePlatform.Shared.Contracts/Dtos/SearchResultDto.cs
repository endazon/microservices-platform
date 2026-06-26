namespace KnowledgePlatform.Shared.Contracts.Dtos;

// FR-03, FR-04: 検索結果と AI 回答の複合レスポンス
public class SearchResultDto
{
    public string Query { get; init; } = string.Empty;
    public List<ChunkDto> Chunks { get; init; } = [];
    public string? AiAnswer { get; init; }
    public List<string> SourceUris { get; init; } = [];
    public int TotalCount { get; init; }
}
