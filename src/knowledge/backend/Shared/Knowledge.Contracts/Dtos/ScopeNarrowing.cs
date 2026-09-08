using Platform.Shared.Contracts.Dtos;

namespace Knowledge.Contracts.Dtos;

// FR-03, FR-05, FR-07, FR-19, NFR-09, UC-01, UC-02, SC-01, SC-08, ADR-0004, ADR-0036,
// ADR-0043, ADR-0046 D-06, [[IADR-0012]], [[IADR-0151]], [[IADR-0253]] 決定 1・2,
// [[IADR-0410]], [[IADR-0415]] (#1340):
// **呼び出し元の指定を ABAC 許可スコープへ交差させる規則の唯一の実装。**
//
// 🔴 **不変条件（最重要）: 実効スコープは ABAC 許可スコープの部分集合である。**
// 指定はあくまで**絞り込み（narrowing）**であり、**権限を一切広げない**。
//
// 🔴 **なぜ共有点に在るのか。** 従前この規則は `AiAnalysisService.Domain.DataRangeScopeResolver`
// にだけ在り、**受け口（RetrievalService）には無かった**。その非対称が #1340 である ——
// `HybridSearchService.BuildFilters` は利用者指定と ABAC 許可を**同じキーの下へ union して**
// おり、**指定するだけで許可値集合が広がっていた**（認証済み利用者が権限外の文書を読めた。実測）。
// **規則が 1 か所にしか無いことが、もう 1 か所での欠落を許した。**
//
// 評価意味論（検索側と一致）: フィルタ間は AND、値集合内は OR。
//   - ABAC が制約するキー ∧ 指定が同キーを指定 → 値集合の積（A ∩ U）。**空なら全体を deny**
//   - ABAC が制約するキーのみ                 → ABAC の値集合をそのまま維持
//   - 指定のみが指定するキー                   → そのまま追加（ABAC は当該キーを無制約に許可して
//                                                いたため、指定で絞るのは安全な narrowing）
public static class ScopeNarrowing
{
    /// <summary>
    /// 許可スコープ（`AccessScope`）へ指定を交差させる。
    /// 🔴 **受け口はこちらを使う** —— 呼び出し元から届くのは `AccessScope` である。
    /// </summary>
    public static AccessScope Apply(
        AccessScope? allowed, IReadOnlyDictionary<string, List<string>>? requested)
    {
        if (allowed is not { GrantsAccess: true })
            return new AccessScope([], false);

        return Resolve(
            new AccessScopeResponse(string.Empty, allowed.Filters, true, allowed.Branches),
            requested);
    }

    /// <summary>
    /// FR-05, NFR-09, ADR-0034 決定 1, [[IADR-0410]], [[IADR-0416]] (#1339):
    /// **権威側のスコープへ、呼び出し元が主張したスコープを絞り込みとして交差させる。**
    ///
    /// 🔴 **呼び出し元の主張は権限の根拠ではない。** 受け口が自分で引いた許可が権威であり、
    /// 主張は**そこから狭める方向にしか効かない**。正直な呼び出し元にとって結果は変わらない
    /// （送ってくるのは同じ許可、あるいはそれを絞ったものだからである）。
    ///
    /// 🔴 **分岐は名前で対応づける。キー単位 union へ畳まない**（[[IADR-0253]] 決定 2 の反例）。
    /// 分岐は同じ許可から導かれているので**名前が一致する** ——
    /// 呼び出し元が落とした分岐は落とし、呼び出し元にしか無い分岐は無視する（広げられない）。
    /// </summary>
    /// <summary>
    /// 認可サービスの応答を権威として、呼び出し元の主張を絞り込みとして交差させる。
    /// 🔴 **受け口はこれを使う** —— 解決したての応答をそのまま渡せる（変換の口を増やさない）。
    /// </summary>
    public static AccessScope Apply(AccessScopeResponse allowed, AccessScope? requested)
        => Apply(
            new AccessScope(allowed.AllowedFilters, allowed.Granted, allowed.Branches),
            requested);

    public static AccessScope Apply(AccessScope? allowed, AccessScope? requested)
    {
        if (allowed is not { GrantsAccess: true })
            return new AccessScope([], false);

        // 主張が無い（未指定・deny）＝絞り込みが無い。**権威をそのまま使う**
        // —— 🔴 ここを deny にすると、主張を送らない呼び出し元が何も引けなくなる。
        if (requested is not { GrantsAccess: true })
            return allowed;

        // 権威側に分岐が無い場合。
        if (allowed.Branches is not { Count: > 0 })
        {
            // 🔴 **主張が分岐を持つなら、分岐のまま残す**（[[IADR-0253]] 決定 2）。
            // ここで主張の平坦な `Filters`（＝分岐のキー単位 union）へ畳むと、
            // **どちらのポリシー単独も許可しない混成を許してしまう** ——
            // A={confidentiality:internal, dept:hr} と B={confidentiality:public, dept:sales} を
            // union すると (internal, sales) が通る。**選言は選言のまま運ぶ。**
            if (requested.Branches is { Count: > 0 })
            {
                var narrowedBranches = new List<AccessScopeBranch>();
                foreach (var branch in requested.Branches)
                {
                    // 各分岐を権威側の平坦な許可で絞る（権威が制約するキーだけが効く）。
                    var narrowed = Apply(
                        new AccessScope(branch.Filters, true), ToRequest(allowed.Filters));
                    if (narrowed.GrantsAccess)
                        narrowedBranches.Add(new AccessScopeBranch(branch.Name, narrowed.Filters));
                }

                if (narrowedBranches.Count == 0)
                    return new AccessScope([], false);

                return new AccessScope(
                    UnionByKey([.. narrowedBranches.Select(b => (b.Name, b.Filters))]),
                    true, narrowedBranches);
            }

            return Apply(allowed, ToRequest(requested.Filters));
        }

        // 権威側に分岐がある。主張にも分岐があれば**名前で対応づけて**交差させる。
        if (requested.Branches is not { Count: > 0 })
            // 主張が平坦（旧算出値だけ）なら、各分岐へ同じ narrowing を当てる。
            return Apply(allowed, ToRequest(requested.Filters));

        var byName = requested.Branches
            .GroupBy(b => b.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var survivors = new List<AccessScopeBranch>();
        foreach (var branch in allowed.Branches)
        {
            // 🔴 呼び出し元が落とした分岐は落とす（narrowing として正当）。
            if (!byName.TryGetValue(branch.Name, out var asked)) continue;

            var narrowed = Apply(
                new AccessScope(branch.Filters, true), ToRequest(asked.Filters));

            // 積が空の分岐だけを捨てる（他の根拠は生きている）。
            if (narrowed.GrantsAccess)
                survivors.Add(new AccessScopeBranch(branch.Name, narrowed.Filters));
        }

        // 全分岐が消えた = どの許可根拠でも主張の外。安全側に倒し全体 deny。
        if (survivors.Count == 0)
            return new AccessScope([], false);

        return new AccessScope(UnionByKey([.. survivors.Select(b => (b.Name, b.Filters))]), true, survivors);
    }

    // 主張のフィルタ列を narrowing の指定（キー → 値集合）へ写す。
    private static Dictionary<string, List<string>>? ToRequest(IReadOnlyList<AttributeFilter>? filters)
        => filters is { Count: > 0 }
            ? filters.GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.SelectMany(f => f.AllowedValues).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    StringComparer.OrdinalIgnoreCase)
            : null;

    /// <summary>
    /// 単値の指定（FR-03 後方互換の `AttributeFilters`）を交差させる。
    /// 🔴 **単値でも規則は同じである** —— 1 要素の値集合として扱うだけで、
    /// 「単値だから素通し」という別の枝を作らない。
    /// </summary>
    public static AccessScope Apply(
        AccessScope? allowed, IReadOnlyDictionary<string, string>? requested)
        => Apply(allowed, requested is { Count: > 0 }
            ? requested.ToDictionary(kv => kv.Key, kv => new List<string> { kv.Value })
            : null);

    /// <summary>
    /// 認可サービスの応答（`AccessScopeResponse`）へ指定を交差させる。
    /// 🔴 **呼び出し元（AiAnalysis）はこちらを使う** —— 解決したての応答をそのまま渡せる。
    /// </summary>
    public static AccessScope Resolve(
        AccessScopeResponse abac, IReadOnlyDictionary<string, List<string>>? rangeFilters)
    {
        // FR-05: deny-by-default。許可ポリシーが無ければ、いかなる範囲指定でも何も開放しない。
        if (!abac.Granted)
            return new AccessScope([], false);

        // FR-19, ADR-0036, IADR-0253 決定 1（段 3 / #989）: 分岐があれば**分岐ごとに独立して**交差させる。
        if (abac.Branches is { Count: > 0 })
            return ResolveBranches(abac.Branches, rangeFilters);

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

    // FR-19, ADR-0036, IADR-0253 決定 1（段 3 / #989）: 分岐（read の選言）へ範囲を交差させる。
    //
    // **分岐ごとに独立して絞る。** 1 分岐 = 1 ポリシーの文書条件であり、選言の各項は互いに
    // 独立した許可根拠だからである。
    //   - 分岐が制約するキーを範囲が指定  → 値集合の積。**積が空ならその分岐だけを捨てる**
    //     （全体 deny ではない —— 他の分岐が生きていれば、その根拠での閲覧は依然として正当である）
    //   - 分岐が制約しないキーを範囲が指定 → その分岐へ追加（安全な narrowing）
    //   - **全分岐が消えたときだけ全体 deny**
    //
    // 🔴 **キー単位 union へ畳まない**（IADR-0253 決定 2 の反例）——
    // A={confidentiality:internal, department:hr} と B={confidentiality:public, department:sales}
    // を union すると、**どちらのポリシー単独も許可しない混成 (internal, sales) を許す**。
    //
    // 後段へ渡す `Filters`（従来面）は**生き残った分岐のキー単位 union で作り直す**。
    // 評価器が「AllowedFilters は分岐の union」という関係で作っている面であり、
    // 分岐が減った以上その面も同じ関係のまま狭める（narrowing-only の不変条件を保つ）。
    private static AccessScope ResolveBranches(
        List<AccessScopeBranch> branches, IReadOnlyDictionary<string, List<string>>? rangeFilters)
    {
        // **名前は分岐と一緒に運ぶ。** 添字で後から引くと、捨てた分岐がある時点でずれる。
        var survivors = new List<(string Name, List<AttributeFilter> Filters)>();

        foreach (var branch in branches)
        {
            var byKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in branch.Filters)
                byKey[f.Key] = new List<string>(f.AllowedValues);

            var dropped = false;
            if (rangeFilters is { Count: > 0 })
            {
                foreach (var (key, requested) in rangeFilters)
                {
                    // 空指定は「当該キーで絞らない」とみなす（範囲未指定と同義）。
                    if (requested is not { Count: > 0 })
                        continue;

                    if (byKey.TryGetValue(key, out var allowed))
                    {
                        var intersection = allowed
                            .Where(v => requested.Contains(v, StringComparer.OrdinalIgnoreCase))
                            .ToList();

                        // 積が空 = この分岐は範囲の外。**この分岐だけを捨てる。**
                        if (intersection.Count == 0)
                        {
                            dropped = true;
                            break;
                        }

                        byKey[key] = intersection;
                    }
                    else
                    {
                        // この分岐が当該キーを無制約に許可 → 範囲で絞るのは narrowing なので安全に追加。
                        byKey[key] = requested
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    }
                }
            }

            if (!dropped)
                survivors.Add((branch.Name,
                    [.. byKey.Select(kv => new AttributeFilter(kv.Key, kv.Value))]));
        }

        // 全分岐が消えた = どの許可根拠でも範囲の外。安全側に倒し全体 deny（漏えい防止）。
        if (survivors.Count == 0)
            return new AccessScope([], false);

        return new AccessScope(
            UnionByKey(survivors),
            true,
            [.. survivors.Select(s => new AccessScopeBranch(s.Name, s.Filters))]);
    }

    // 生き残った分岐のキー単位 union（従来面 Filters の作り直し）。
    private static List<AttributeFilter> UnionByKey(
        List<(string Name, List<AttributeFilter> Filters)> branches)
    {
        var byKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var branch in branches)
            foreach (var f in branch.Filters)
            {
                if (!byKey.TryGetValue(f.Key, out var list))
                    byKey[f.Key] = list = [];
                foreach (var v in f.AllowedValues)
                    if (!list.Contains(v, StringComparer.OrdinalIgnoreCase))
                        list.Add(v);
            }

        return [.. byKey.Select(kv => new AttributeFilter(kv.Key, kv.Value))];
    }
}
