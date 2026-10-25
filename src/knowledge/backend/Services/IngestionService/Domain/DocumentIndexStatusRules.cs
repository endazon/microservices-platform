namespace IngestionService.Domain;

// FR-06, FR-02, SC-05, ADR-0127, [[IADR-0536]], [[IADR-0565]] (#1993): 文書の**索引の状態**（文書管理の一覧の列）。
//
// 計画は列の一覧も状態の分類も定めていない（実装の判断。IADR-0565 決定 1）。3 値は索引の点から導く。
public enum IndexState
{
    // ベクトルのコレクションか語彙索引（埋め込み待ちの印なし）に点がある。高機密の語彙索引だけの文書（ADR-0127）もここ。
    Indexed,

    // 語彙索引に埋め込み待ちの印の点が 1 つでもある（IADR-0536）。一部のチャンクだけが埋め込めた文書もここ。
    EmbeddingPending,

    // どのコレクションにも点が無い（取り込み前・取り込みの失敗・露出の除外・アーカイブで外れた文書など）。
    NotIndexed,
}

// [[IADR-0565]] 決定 1: 索引の点の在りか（文書 ID の集合 2 つ）から状態を決める純関数と、1 回の照会の上限。
public static class DocumentIndexStatusRules
{
    // 1 回の照会で受ける文書 ID の上限。BFF の 1 ページの上限（`DocumentListWindow.MaxLimit`）と同じ。
    // 🔴 超えたら拒む（切り詰めない）—— 切ると、呼び出し元は残りを「未索引」と読み違える。
    public const int MaxDocuments = 100;

    // 🔴 **埋め込み待ちが先。** 一部のチャンクだけが埋め込めた文書は、意味検索には一部しか出ないので「索引済み」と言わない。
    public static IndexState Classify(Guid documentId, DocumentIndexPresence presence) =>
        presence.EmbeddingPending.Contains(documentId) ? IndexState.EmbeddingPending
        : presence.Indexed.Contains(documentId) ? IndexState.Indexed
        : IndexState.NotIndexed;
}

// [[IADR-0565]] 決定 2: 照会した文書のうち、点を持つものの集合。
// `Indexed` = ベクトルのコレクションか語彙索引（印なし）に点がある文書、`EmbeddingPending` = 語彙索引に印ありの点がある文書。
public sealed record DocumentIndexPresence(IReadOnlySet<Guid> Indexed, IReadOnlySet<Guid> EmbeddingPending);
