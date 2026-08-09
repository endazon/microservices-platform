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

    // FR-04, FR-05, SC-01, SC-08, #540: 権限内属性値の照会（計画 ADR-0043）。
    // **到達できる文書に実際に付与されている値だけ**を返す（辞書を丸ごと返さない。決定 1）。
    // **件数は返さない**（決定 2）——実装が facet で数えても、値集合だけを返すこと。
    // filters は検索と**同じ ABAC 多値 allow-list** を渡す（別経路で数えると「検索には出るが
    // 候補に無い値」が生まれる。IADR-0151 決定 1）。
    Task<List<string>> ListAttributeValuesAsync(
        string payloadKey,
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
