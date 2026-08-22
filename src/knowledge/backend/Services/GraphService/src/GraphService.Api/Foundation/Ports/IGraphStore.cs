using GraphService.Api.Foundation.Domain;
using GraphService.Api.Foundation.Services;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Api.Foundation.Ports;

// FR-17, UC-10, ADR-0033 未決事項, IADR-0242 決定 11: グラフの読み取りポート。
//
// **ストア製品は計画側で実測待ちのままである**（PostgreSQL の隣接リストか専用グラフ DB か）。
// 待つとすべてがブロックされるため、当面はプラットフォーム標準（EF Core + Npgsql）で実装しつつ、
// 探索を本ポート越しに置いて交換可能に保つ。
//
// 🔴 **LoadIncidentEdgesAsync は IReadOnlyList<AuthorizedNode> しか受け取らない。**
// これが ADR-0034 決定 1（ホップごと判定）の型による強制である —— 非許可ノードから
// 展開することが型として書けない（IADR-0242 決定 2）。署名を GraphDocument や Guid へ
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
    // 利用者から見て「見えたり見えなかったり」する（ADR-0034 決定 4 / IADR-0242 決定 4）。
    Task<IReadOnlyList<Edge>> LoadIncidentEdgesAsync(
        IReadOnlyList<AuthorizedNode> frontier, CancellationToken ct = default);

    // FR-04, ADR-0035 決定 2 (#947a): 文書ごとの次数（接続する辺の本数）。
    // ハブ文書を**展開の中継点にしない**判定に使う。
    //
    // 🔴 **ABAC で絞らない。全辺を数える。**
    // 可視の辺だけを数えると、**同じ文書が利用者によってハブになったりならなかったりする** ——
    // ハブ判定はグラフの構造上の性質であって、利用者の権限の関数ではない。
    //
    // 🔴 **次数は応答に現れない。** #962 で「使用件数を一般利用者へ返さない」と判断したのとは
    // 別の話である（あちらは集計値を**応答に載せる**話、こちらは**内部の展開判断**に使う話）。
    Task<IReadOnlyDictionary<Guid, int>> LoadDegreesAsync(
        IReadOnlyCollection<Guid> documentIds, CancellationToken ct = default);

    // FR-18, ADR-0051 決定 3, ADR-0033 決定 7, IADR-0266 決定 2 (#915):
    // **AI 提案の候補列挙。スコープ述語をこの段で適用する。**
    //
    // 🔴 **戻り値は IReadOnlyList<AuthorizedNode> しかない。** ADR-0051 決定 3 は順序を
    // 「全文書横断で類似度 → **スコープで絞る** → LLM へ渡す」と定め、**絞りを LLM 呼び出しより
    // 後ろに置くことを禁じた。** 本口が非許可ノードを返さないことで、「LLM へ渡してから捨てる」形が
    // 呼び出し側で書けなくなる。**署名を GraphDocument や Guid へ緩めてはならない。**
    //
    // 同じ段で次も落とす（DB から出さない）。
    //   - 起点自身
    //   - **却下済みの組み合わせ**（ADR-0033 決定 7「却下した組み合わせは以後の提案生成で候補から除外」）
    //   - 既に辺がある組み合わせ・既に提案がある組み合わせ（二重提案の防止）
    //
    // 🔴 **落とした件数を返さない。** ADR-0051 決定 2 の「件数・存在も出さない」に従う。
    Task<IReadOnlyList<AuthorizedNode>> EnumerateAuthorizedCandidatesAsync(
        Guid originDocumentId,
        IReadOnlyCollection<Guid> candidateDocumentIds,
        AccessScopeResponse scope,
        CancellationToken ct = default);
}
