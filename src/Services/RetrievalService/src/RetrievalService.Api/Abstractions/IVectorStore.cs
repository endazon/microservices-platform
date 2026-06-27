using KnowledgePlatform.Shared.Contracts.Dtos;

namespace RetrievalService.Api.Abstractions;

// FR-03, ADR-0009: ベクトルDBポート（製品差し替え可能な抽象化）
public interface IVectorStore
{
    // FR-03: 意味検索（密ベクトル類似度）
    Task<List<SearchResultDto>> SearchAsync(
        float[] queryVector,
        int topK,
        Dictionary<string, string>? attributeFilters,
        CancellationToken ct = default);

    // FR-03: 全文検索（キーワード／語句一致）。ハイブリッド検索の全文側を担う。
    Task<List<SearchResultDto>> KeywordSearchAsync(
        string query,
        int topK,
        Dictionary<string, string>? attributeFilters,
        CancellationToken ct = default);

    Task UpsertAsync(ChunkPayload chunk, CancellationToken ct = default);

    Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct = default);
}

public record ChunkPayload(
    Guid ChunkId,
    Guid DocumentId,
    string DocumentTitle,
    string Text,
    float[] Vector,
    string? MarkdownUri,
    Dictionary<string, string> Attributes,
    List<string> Tags);
