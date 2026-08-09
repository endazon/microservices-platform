using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;

namespace RetrievalService.Api.Foundation.Ports;

// FR-03, ADR-0009: ベクトルDBポート（製品差し替え可能な抽象化）
public interface IVectorStore
{
    // FR-03: 意味検索（密ベクトル類似度）
    // FR-05: filters は ABAC 多値 allow-list（key ∈ values の AND 結合）。
    Task<List<SearchResultDto>> SearchAsync(
        float[] queryVector,
        int topK,
        IReadOnlyList<AttributeFilter>? filters,
        CancellationToken ct = default);

    // FR-03: 全文検索（キーワード／語句一致）。ハイブリッド検索の全文側を担う。
    // FR-05: filters は ABAC 多値 allow-list（key ∈ values の AND 結合）。
    Task<List<SearchResultDto>> KeywordSearchAsync(
        string query,
        int topK,
        IReadOnlyList<AttributeFilter>? filters,
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
    List<string> Tags,
    // FR-03, SC-02, #536: 文書の更新日時（IADR-0149）。本番の書き込みは IngestionService が担うが、
    // **同じコレクションを読む復元側と表現を揃える**ため本ポートでも同じ値を運ぶ
    // （表現がずれると「テストは緑・本番は空」になる。IADR-0014 が ABAC 属性で踏んだのと同じ型）。
    DateTimeOffset? UpdatedAt = null);
