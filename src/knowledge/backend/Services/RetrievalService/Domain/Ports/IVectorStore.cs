using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;

namespace RetrievalService.Domain.Ports;

// FR-03, ADR-0009: ベクトルDBポート（製品差し替え可能な抽象化）
public interface IVectorStore
{
    // FR-03: 意味検索（密ベクトル類似度）
    // FR-05, FR-19: filters は ABAC 制約（連言 ＋ 分岐の選言。ScopeFilter を参照）。
    Task<List<SearchResultDto>> SearchAsync(
        float[] queryVector,
        int topK,
        ScopeFilter? filters,
        CancellationToken ct = default);

    // FR-04, FR-17, ADR-0035, #969: 文書 ID 集合に絞った意味検索（二段検索の後段）。
    // グラフ近傍展開が返すのは**文書単位**の候補であり、出典（CitationDto）が要る
    // ChunkId / Score / Snippet を持たない。その文書 ID に絞って本口を走らせることで、
    // チャンク単位のスコアとスニペットを**正規の経路で**得る。
    //
    // 🔴 **`documentIds` が空なら「該当なし」（空リスト）を返す。「全件」ではない**
    // ——空を全件と読むと、グラフが 0 件を返したときに検索が全文書へ広がる。
    // 🔴 **`filters`（ABAC）とは AND で結合する。** 文書 ID による絞りは ABAC を置き換えるものではなく、
    // **追加の制約**である。
    //
    // 既存の `SearchAsync` は変更しない（ADR-0035 決定 1「既存検索の実装は変更せず、後段を足す」）。
    Task<List<SearchResultDto>> SearchWithinDocumentsAsync(
        float[] queryVector,
        int topK,
        IReadOnlyCollection<Guid> documentIds,
        ScopeFilter? filters,
        CancellationToken ct = default);

    // FR-03: 全文検索（キーワード／語句一致）。ハイブリッド検索の全文側を担う。
    // FR-05, FR-19: filters は ABAC 制約（連言 ＋ 分岐の選言。ScopeFilter を参照）。
    Task<List<SearchResultDto>> KeywordSearchAsync(
        string query,
        int topK,
        ScopeFilter? filters,
        CancellationToken ct = default);

    // FR-04, FR-05, SC-01, SC-08, #540: 権限内属性値の照会（計画 ADR-0043）。
    // **到達できる文書に実際に付与されている値だけ**を返す（辞書を丸ごと返さない。決定 1）。
    // **件数は返さない**（決定 2）——実装が facet で数えても、値集合だけを返すこと。
    // filters は検索と**同じ ABAC 多値 allow-list** を渡す（別経路で数えると「検索には出るが
    // 候補に無い値」が生まれる。IADR-0151 決定 1）。
    Task<List<string>> ListAttributeValuesAsync(
        string payloadKey,
        ScopeFilter? filters,
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
    DateTimeOffset? UpdatedAt = null,
    // FR-02, FR-03, SC-02, ADR-0070 決定 4, #1193, [[IADR-0358]] 決定 3: この点が本文由来か。
    //
    // 🔴 **`Text` は「索引テキスト」であって「本文」ではない。** `HasBody = false` の点では
    // 題名由来のメタデータが入り、**検索の突合には使うが利用者へは返さない**
    // （`SearchResultDto.Text` は `DocumentBodyPresence.Excerpt` が空にする）。
    // **本実装と Qdrant 実装の双方が同じ射影を通すこと** —— 片方だけだと
    // 「テストは緑・本番はメタデータが本文として漏れる」になる（IADR-0014 と同型）。
    bool HasBody = true,
    // FR-19, FR-20, ADR-0036 D-06, ADR-0061 決定 5 / [[IADR-0396]] 決定 3 (#1184):
    // 共有先（`shared_with`）。**属性辞書ではなく `Tags` と同じリスト**で持つ ——
    // 単一値では集合を表せないためで、絞り込みは「いずれか一致」になる
    // （`AttributeValueKeys.IsListValued` が両実装の意味論を 1 つに保つ）。
    List<string>? SharedWith = null);
