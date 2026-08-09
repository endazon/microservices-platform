using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;

namespace AiAnalysisService.Api.Foundation.Services;

// FR-07, FR-05, UC-02: 利用者が指定したデータ範囲を ABAC 許可スコープと交差させ、
// 検索へ渡す「実効アクセススコープ」を導出する純粋ロジック。
//
// 不変条件（最重要）: 実効スコープは ABAC 許可スコープの「部分集合」でなければならない。
//   データ範囲はあくまで絞り込み（narrowing）であり、権限を一切広げない。
//   これにより受け入れ基準②「権限の無い文書は検索結果・AI 回答のいずれにも一切現れない」を保証する。
//
// 評価意味論（retrieval と一致）: フィルタ間は AND、値集合内は OR。
//   - ABAC が制約するキー  ∧ 範囲が同キーを指定 → 値集合の積（A ∩ U）。空なら全体を deny。
//   - ABAC が制約するキー  のみ                 → ABAC の値集合をそのまま維持。
//   - 範囲のみが指定するキー                     → そのまま追加（ABAC は当該キーを無制約に許可していたため、
//                                                  範囲で絞るのは安全な narrowing）。
public static class DataRangeScopeResolver
{
    public static AccessScope Resolve(AccessScopeResponse abac, AnalysisDataRange? range)
        => Resolve(abac, range?.AttributeFilters);

    // FR-04, SC-01, SC-08, #539: 対象範囲を**データ範囲の器から切り離して**受ける。
    //
    // SC-01（検索・質問）と SC-08（AI 分析）は「同じ『範囲を絞る』操作」であり、
    // **画面ごとに違う挙動になると利用者は操作を覚え直すことになる**（計画 L342・裁定 Q3）。
    // `/analysis/ask` は `AnalysisDataRange`（`Query` / `TopK` を持つ）を取らないので、
    // **交差の規則だけを共有する**——規則を 2 本持つと、片方だけ直したときに
    // 「検索では効くが分析では効かない」という食い違いが生まれる。
    public static AccessScope Resolve(
        AccessScopeResponse abac, IReadOnlyDictionary<string, List<string>>? rangeFilters)
    {
        // FR-05: deny-by-default。許可ポリシーが無ければ、いかなる範囲指定でも何も開放しない。
        if (!abac.Granted)
            return new AccessScope([], false);

        var byKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in abac.AllowedFilters)
            byKey[f.Key] = new List<string>(f.AllowedValues);

        if (rangeFilters is { Count: > 0 })
        {
            foreach (var (key, requested) in rangeFilters)
            {
                // 空指定は「当該キーで絞らない」とみなす（範囲未指定と同義）。
                if (requested is not { Count: > 0 })
                    continue;

                if (byKey.TryGetValue(key, out var allowed))
                {
                    // 交差: ABAC が許可する値のうち、範囲が要求するものだけを残す。
                    var intersection = allowed
                        .Where(v => requested.Contains(v, StringComparer.OrdinalIgnoreCase))
                        .ToList();

                    // 積が空 = 範囲が権限の外を指している。安全側に倒し全体を deny（漏えい防止）。
                    if (intersection.Count == 0)
                        return new AccessScope([], false);

                    byKey[key] = intersection;
                }
                else
                {
                    // ABAC が当該キーを無制約に許可 → 範囲で絞るのは narrowing なので安全に追加。
                    byKey[key] = requested
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }
        }

        var filters = byKey
            .Select(kv => new AttributeFilter(kv.Key, kv.Value))
            .ToList();

        return new AccessScope(filters, true);
    }
}
