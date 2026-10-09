namespace GraphService.Domain;

// FR-17, ADR-0035 決定 1, [[IADR-0522]] (#1396): 共有タグの辺の**規則**（純粋関数。DB を見ない）。
//
// - 2 文書が**ハブでない**タグを 1 つ以上共有するとき、その組を結ぶ（組あたり辺は 1 本）。
// - **ハブ**: 文書数が上限を**超える**タグ。辺を作らない。N 文書のタグは N(N−1)/2 本を作り、
//   所属文書の次数はそれだけで N−1 になる。上限の既定は探索のハブ次数上限（`GraphTraversal.MaxHubDegree`）と
//   同じ値であり、上限を超えたタグの所属文書は探索の中継点として使われない（ADR-0035 決定 1）——
//   行数だけが二乗で増え、探索に効かない。
public static class TagEdgeRule
{
    public static bool IsHub(int documentCount, int maxDocumentsPerTag) => documentCount > maxDocumentsPerTag;

    // `member` が結ばれるべき相手の集合。
    // `membersOf` はタグ → 所属文書（`member` 自身を含んでよい）。ハブのタグは引かない（呼び出し側が
    // 所属を読み込まずに済むよう、件数で先に判定する）。
    public static HashSet<Guid> DesiredPartners(
        Guid member,
        IEnumerable<string> tagsOfMember,
        Func<string, int> countOf,
        Func<string, IReadOnlyCollection<Guid>> membersOf,
        int maxDocumentsPerTag)
    {
        var partners = new HashSet<Guid>();
        foreach (var tag in tagsOfMember)
        {
            if (IsHub(countOf(tag), maxDocumentsPerTag))
                continue;
            foreach (var other in membersOf(tag))
                if (other != member)
                    partners.Add(other);
        }
        return partners;
    }
}
