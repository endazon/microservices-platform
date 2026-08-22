using GraphService.Api.Foundation.Domain;
using GraphService.Api.Foundation.Services;

namespace GraphService.Api.Foundation.Ports;

// FR-17, UC-10, ADR-0033 未決事項, IADR-0238 決定 11: グラフの読み取りポート。
//
// **ストア製品は計画側で実測待ちのままである**（PostgreSQL の隣接リストか専用グラフ DB か）。
// 待つとすべてがブロックされるため、当面はプラットフォーム標準（EF Core + Npgsql）で実装しつつ、
// 探索を本ポート越しに置いて交換可能に保つ。
//
// 🔴 **LoadIncidentEdgesAsync は IReadOnlyList<AuthorizedNode> しか受け取らない。**
// これが ADR-0034 決定 1（ホップごと判定）の型による強制である —— 非許可ノードから
// 展開することが型として書けない（IADR-0238 決定 2）。署名を GraphDocument や Guid へ
// 緩めてはならない。
public interface IGraphStore
{
    // 起点ノードを 1 件読む。存在しなければ null。
    // **「存在しない」と「権限が無い」を呼び出し側で同じ 404 に倒すこと**（ADR-0034 決定 2）。
    Task<GraphDocument?> FindNodeAsync(Guid documentId, CancellationToken ct = default);

    // 指定した文書 ID の集合のノードを一括で読む（属性込み）。
    Task<IReadOnlyList<GraphDocument>> LoadNodesAsync(
        IReadOnlyCollection<Guid> documentIds, CancellationToken ct = default);

    // UC-10: フロンティアに接続する辺を双方向に一括で読む（#909 の探索が使う）。
    // バックリンク（FR-17）のため source 側・target 側の両方を引く。
    // 決定的順序（CreatedAt, Id）で返す —— 打ち切りが非決定だとテストが flake し、
    // 利用者から見て「見えたり見えなかったり」する（ADR-0034 決定 4 / IADR-0238 決定 4）。
    Task<IReadOnlyList<Edge>> LoadIncidentEdgesAsync(
        IReadOnlyList<AuthorizedNode> frontier, CancellationToken ct = default);
}
