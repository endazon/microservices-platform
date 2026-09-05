namespace GraphService.Domain;

// FR-10, FR-17, UC-10, ADR-0033 決定 4, [[IADR-0281]], [[IADR-0389]] (#1246):
// リンク先の名前 → 文書 ID の**解決規則そのもの**。純粋関数であり DB を触らない。
//
// ## なぜ切り出すか
//
// 規則の**使い手が 2 つある**:
//   1. `LinkEdgeSynchronizer` — 辺を張るために解決する（従来からの使い手）。
//   2. `KnowledgeHealthCollector` — **解決できないリンクを数える**ために解決する（#1246 で追加）。
//
// 🔴 **両者が別々に規則を書くと、片方だけを直したときに「辺は張られないのに未解決にも数えられない」
// リンクが生まれる。** 指標が測っている対象と、実際に辺が作られなかった対象がずれる ——
// 指標としては最悪の壊れ方（数字は出るが意味が無い）である。**規則は 1 か所にしか置かない。**
//
// ## 規則（従前の `LinkEdgeSynchronizer.ResolveTargetsAsync` と同一の意味）
//
//   [1] ordinal 完全一致が **1 件**       → 解決
//   [2] ordinal 完全一致が **2 件以上**   → 曖昧
//   [3] ordinal 完全一致が 0 件で、
//       大文字小文字を無視した一致が 1 件 → 解決
//   [4] 同 2 件以上                       → 曖昧
//   [5] どちらも 0 件                     → 不在
//
// ⚠️ **[2] は短絡であって規則ではない。** 大文字小文字を無視した一致は ordinal 一致の**上位集合**
// であり（ordinal で等しければ ci でも等しい）、`exact >= 2` なら `loose >= 2` が必ず成り立つ。
// つまり [2] を消して [3][4] へ降ろしても**結論は変わらない。**
// 🔴 **これは変異試験で実測した**（[2] を削っても 21 件すべてが緑のまま。#1246）。
// 「降りると解決に化ける」という直感は誤りであり、そう読める主張をテストへ書かない。
// 短絡として残すのは、従前の実装の形と読みやすさのためである。
internal static class LinkTargetMatcher
{
    // 解決の結果。**曖昧と不在を潰さない** —— [[IADR-0389]] 決定 2 が両方を未解決に数えつつ、
    // 内訳の軸で分けると決めたためである（運用の直し方が違う。改名 vs 作成）。
    internal enum LinkTargetOutcome
    {
        Resolved,
        NotFound,
        Ambiguous,
    }

    // 観測値の**内訳の軸**に載せる値。受け口の軸は自由語だが、ここで閉じた 2 語に固定する
    // （綴りが揺れると内訳が静かに分裂する）。
    internal const string NotFoundDimension = "not-found";
    internal const string AmbiguousDimension = "ambiguous";

    internal readonly record struct LinkTargetMatch(LinkTargetOutcome Outcome, Guid DocumentId)
    {
        internal bool IsResolved => Outcome == LinkTargetOutcome.Resolved;

        // 未解決のときの軸。解決済みでは null。
        internal string? Dimension => Outcome switch
        {
            LinkTargetOutcome.NotFound => NotFoundDimension,
            LinkTargetOutcome.Ambiguous => AmbiguousDimension,
            _ => null,
        };
    }

    // 突合の候補 1 件（文書 ID と題名）。呼び出し側が DB からどう絞って来たかは問わない。
    internal readonly record struct TitleCandidate(Guid DocumentId, string Title);

    internal static LinkTargetMatch Match(string target, IReadOnlyList<TitleCandidate> candidates)
    {
        var exact = Count(candidates, target, StringComparison.Ordinal);
        if (exact.Count == 1) return new(LinkTargetOutcome.Resolved, exact.Single);
        if (exact.Count > 1) return new(LinkTargetOutcome.Ambiguous, Guid.Empty);

        var loose = Count(candidates, target, StringComparison.OrdinalIgnoreCase);
        if (loose.Count == 1) return new(LinkTargetOutcome.Resolved, loose.Single);
        if (loose.Count > 1) return new(LinkTargetOutcome.Ambiguous, Guid.Empty);

        return new(LinkTargetOutcome.NotFound, Guid.Empty);
    }

    // 一致件数と（1 件なら）その ID。**2 件見つかった時点で数え終える必要は無い** ——
    // 候補は 1 つのリンク先ぶんであり、全件走査しても短い。
    private static (int Count, Guid Single) Count(
        IReadOnlyList<TitleCandidate> candidates, string target, StringComparison comparison)
    {
        var count = 0;
        var single = Guid.Empty;
        foreach (var c in candidates)
        {
            if (!string.Equals(c.Title, target, comparison)) continue;
            count++;
            if (count == 1) single = c.DocumentId;
        }
        return (count, single);
    }
}
