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

// FR-04: 出典（番号付き＋元文書へのリンク）
// AI 回答中の [1][2] と対応する根拠。利用者は SourceUri から元文書へ辿れる。
public record CitationDto(
    int Number,
    Guid DocumentId,
    string DocumentTitle,
    Guid ChunkId,
    string? SourceUri,
    float Score,
    string Snippet);

// FR-04: RAG 回答レスポンス（回答本文＋番号付き出典）
public record AiAnswerDto(
    string Answer,
    List<CitationDto> Citations,
    string Model,
    int InputTokens,
    int OutputTokens);
