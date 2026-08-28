namespace RetrievalService.Domain.Ports;

// FR-04, FR-17, UC-10, ADR-0035 決定 1・2 (#970): 二段検索の段② —— **グラフ近傍展開のポート。**
//
// 段② は「ベクトル検索の上位 N 件の文書 ID を起点に、グラフを近傍展開する」だけを担う。
// 到達した文書を**チャンク単位の出典へ変換する**のは段③（`IVectorStore.SearchWithinDocumentsAsync`。
// IADR-0259）であり、本ポートは**スコープもスニペットも扱わない**。
//
// 🔴 **本ポートは ABAC を判定しない。** 判定するのは GraphService 側であり（ホップごと ABAC・
// IADR-0242）、実装（HTTP アダプタ）は**呼び出し元の `Authorization` ヘッダを伝播する**ことで
// それを効かせる（方式 A。#916a の判断規則「下流が自分で解決する型なら A」）。
// **解決済み scope を本文で渡す方式 B は採らない** —— 下流に権限昇格の口を開けるためである。
public interface IGraphNeighborExpander
{
    // 起点集合から hops ホップの近傍を取り、**辺の集合**として返す。
    // 失敗（下流不達・資格情報なし）は例外にせず空を返す —— 検索そのものは成立させる。
    Task<GraphNeighborhood> ExpandAsync(
        IReadOnlyList<Guid> seedDocumentIds,
        int hops,
        CancellationToken ct = default);
}

// FR-04, FR-17, ADR-0035 決定 2 (#970): 近傍の 1 辺。**辺の型の重みを運ぶ**（再ランクが使う）。
//
// 🔴 **辺は無向として扱う。** GraphService の探索はバックリンクを含めて双方向にロードしており
// （`GraphTraversal.LoadIncidentEdgesAsync`）、到達可能性の意味で向きは既に潰れている。
public sealed record GraphNeighborEdge(Guid SourceDocumentId, Guid TargetDocumentId, double Weight);

// FR-17 (#970): 近傍探索の結果。
//
// 🔴 **ノード一覧を持たない。これは省略ではなく意図した除外である。**
// 候補になれるのは**辺で到達した文書だけ**とする —— 未承認（pending / rejected）の AI 提案は
// 辺として存在しない（#914 の状態機械）ので、**辺だけを入口にすれば構造的に混ざり得ない**。
// ノード一覧を候補の入口にすると、その構造的な保証が「実装が正しく除いているか」の話に落ちる。
public sealed record GraphNeighborhood(IReadOnlyList<GraphNeighborEdge> Edges)
{
    public static readonly GraphNeighborhood Empty = new([]);
}
