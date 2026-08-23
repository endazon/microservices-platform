using GraphService.Api.Foundation.Domain;
using GraphService.Api.Foundation.Ports;
using GraphService.Api.Foundation.Services;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Api.Foundation.Persistence;

// FR-17, UC-10, IADR-0242 決定 11: EF Core（PostgreSQL 隣接リスト）によるグラフ読み取り。
//
// ストア製品は計画側で実測待ちのままであり、本実装はポート越しに置いて交換可能に保つ。
public class EfGraphStore(GraphDbContext db) : IGraphStore
{
    public async Task<GraphDocument?> FindNodeAsync(Guid documentId, CancellationToken ct = default)
        => await db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.DocumentId == documentId, ct);

    public async Task<IReadOnlyList<GraphDocument>> LoadNodesAsync(
        IReadOnlyCollection<Guid> documentIds, CancellationToken ct = default)
    {
        if (documentIds.Count == 0)
            return [];

        return await db.Documents.AsNoTracking()
            .Where(d => documentIds.Contains(d.DocumentId))
            .ToListAsync(ct);
    }

    // UC-10: フロンティアに接続する辺を**双方向**に一括で読む。
    //
    // 🔴 **引数は AuthorizedNode である。** 非許可ノードから展開できないことを型が保証する
    // （ADR-0034 決定 1 / IADR-0242 決定 2）。署名を Guid へ緩めると型ゲートが無効になる。
    //
    // バックリンク（FR-17）のため source 側・target 側の両方を引く。対称型は書き込み時に
    // (min, max) へ正規化済みなので、双方向に引けば向きに関わらず拾える。
    public async Task<IReadOnlyList<Edge>> LoadIncidentEdgesAsync(
        IReadOnlyList<AuthorizedNode> frontier, CancellationToken ct = default)
    {
        if (frontier.Count == 0)
            return [];

        var ids = frontier.Select(n => n.DocumentId).ToList();

        // ADR-0034 決定 4 / IADR-0242 決定 4: **決定的順序**で返す。
        // 打ち切りが非決定だとテストが flake し、利用者から見て「見えたり見えなかったり」する。
        return await db.Edges.AsNoTracking()
            .Where(e => ids.Contains(e.SourceDocumentId) || ids.Contains(e.TargetDocumentId))
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)
            .ToListAsync(ct);
    }

    // FR-04, ADR-0035 決定 2 (#947a): 文書ごとの次数。
    //
    // 🔴 **ABAC で絞らない。** 可視の辺だけを数えると、同じ文書が利用者によってハブになったり
    // ならなかったりする。ハブ判定はグラフの構造上の性質である（ポートの注記参照）。
    //
    // 対称辺は (min, max) の 1 行で持つので、source 側・target 側の両方を数えれば
    // 向きに関わらず 1 本として数えられる。**自己ループは持たない**（作成時に拒む）ので
    // 二重計上は起きない。
    public async Task<IReadOnlyDictionary<Guid, int>> LoadDegreesAsync(
        IReadOnlyCollection<Guid> documentIds, CancellationToken ct = default)
    {
        if (documentIds.Count == 0)
            return new Dictionary<Guid, int>();

        var ids = documentIds.ToList();

        var asSource = await db.Edges.AsNoTracking()
            .Where(e => ids.Contains(e.SourceDocumentId))
            .GroupBy(e => e.SourceDocumentId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var asTarget = await db.Edges.AsNoTracking()
            .Where(e => ids.Contains(e.TargetDocumentId))
            .GroupBy(e => e.TargetDocumentId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var degrees = new Dictionary<Guid, int>();
        foreach (var id in ids) degrees[id] = 0;
        foreach (var r in asSource) degrees[r.Id] = degrees.GetValueOrDefault(r.Id) + r.Count;
        foreach (var r in asTarget) degrees[r.Id] = degrees.GetValueOrDefault(r.Id) + r.Count;
        return degrees;
    }
    // FR-18, ADR-0051 決定 3, ADR-0033 決定 7, IADR-0266 決定 2 (#915):
    // **AI 提案の候補列挙。スコープ述語をこの段で適用する。**
    //
    // 🔴 **ABAC 述語を SQL へ押し込めないことを、ごまかさずに書いておく。**
    // graph_documents.attributes は値変換（jsonb ↔ Dictionary<string,string>）で持っており、
    // AbacNodeFilter の意味論（キー欠落は不一致・値集合内 OR・フィルタ間 AND）を EF が翻訳できない。
    // **本番の Npgsql でもテストの InMemory でも同じ制約である。**
    //
    // **それでも「候補列挙の段で絞った」と言えるのは、非許可ノードを持つ値が LLM 呼び出しの引数として
    // 存在し得ないからである** —— 本メソッドの戻り値の型が AuthorizedNode であり、呼び出し側は
    // これを封（SuggestionPrompt.Seal）へ渡す以外に LLM へ届ける経路を持たない。
    // ADR-0051 決定 3 が禁じたのは「絞りを LLM 呼び出しより**後ろ**に置く」ことであって
    // 「SQL で絞る」ことではない。
    public async Task<IReadOnlyList<AuthorizedNode>> EnumerateAuthorizedCandidatesAsync(
        Guid originDocumentId,
        IReadOnlyCollection<Guid> candidateDocumentIds,
        AccessScopeResponse scope,
        CancellationToken ct = default)
    {
        // FR-05: deny-by-default。許可ポリシーが無ければ候補は 1 件も無い。
        if (!scope.Granted || candidateDocumentIds.Count == 0)
            return [];

        // 起点自身は候補にしない（自己ループは提案しない）。
        var ids = candidateDocumentIds
            .Where(id => id != originDocumentId && id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return [];

        // ADR-0033 決定 7: **却下済みの組み合わせは以後の提案生成で候補から除外する。**
        // 状態を問わず既存のリンク提案があるものを外す —— pending / approved も二重提案になる。
        var existing = await db.AiSuggestions.AsNoTracking()
            .Where(s => s.Kind == SuggestionKind.Link
                     && (s.SourceDocumentId == originDocumentId
                         || (s.TargetDocumentId != null && s.TargetDocumentId == originDocumentId)))
            .Select(s => new { s.SourceDocumentId, s.TargetDocumentId })
            .ToListAsync(ct);

        var excluded = new HashSet<Guid>();
        foreach (var e in existing)
        {
            if (e.SourceDocumentId == originDocumentId && e.TargetDocumentId is { } t)
                excluded.Add(t);
            else if (e.TargetDocumentId == originDocumentId)
                excluded.Add(e.SourceDocumentId);
        }

        // 既に辺がある組み合わせも提案しない（確定済みの関係を提案し直さない）。
        var linked = await db.Edges.AsNoTracking()
            .Where(e => e.SourceDocumentId == originDocumentId || e.TargetDocumentId == originDocumentId)
            .Select(e => new { e.SourceDocumentId, e.TargetDocumentId })
            .ToListAsync(ct);
        foreach (var e in linked)
            excluded.Add(e.SourceDocumentId == originDocumentId ? e.TargetDocumentId : e.SourceDocumentId);

        var wanted = ids.Where(id => !excluded.Contains(id)).ToList();
        if (wanted.Count == 0)
            return [];

        var rows = await db.Documents.AsNoTracking()
            .Where(d => wanted.Contains(d.DocumentId))
            // 決定的順序（IADR-0242 決定 4 と同じ理由。並びが非決定だとテストが flake する）。
            .OrderBy(d => d.DocumentId)
            .ToListAsync(ct);

        // 🔴 **ここが述語の適用点である。** 非許可ノードは黙って落ちる —— **件数も返さない**
        // （ADR-0051 決定 2 / ADR-0034 決定 2「見えない辺は完全に隠す」）。
        return AuthorizedNode.AuthorizeAll(rows, scope);
    }
}
