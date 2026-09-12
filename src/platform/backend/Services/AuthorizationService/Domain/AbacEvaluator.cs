using Platform.Shared.Contracts.Dtos;

namespace AuthorizationService.Domain;

// FR-05, ADR-0004: ABAC ポリシー評価エンジン（deny by default）
public class AbacEvaluator
{
    // FR-19, 計画 ADR-0036 D-03, ADR-0098 決定 1, [[IADR-0447]] (#1447):
    // 認可判定に使える束縛変数。
    //
    // ★［2026-09-12 / #1447］**2 つである。** 従前ここには「1 つだけである。増やさない —— 計画が
    // 束縛変数の語彙を定めていないため、実装が先取りしない」と書いてあった（`IADR-0253` 決定 3）。
    // その理由づけは**計画の側で解消した**: 計画 `ADR-0036` D-03 が束縛変数を
    // `${current_user}` と `${current_groups}` の 2 つと明記し、`ADR-0098` 決定 1 が
    // `${current_groups}` の値を **Keycloak のグループ ID** と定めた
    // （判定規則は `07_abac-attribute-model` §動的束縛
    // `doc.shared_with ∩ ({${current_user}} ∪ ${current_groups}) ≠ ∅`）。
    //
    // 🔴 **語彙はなお計画が定める。** 3 つ目を実装の判断で足してはならない ——
    // 増やすなら計画 ADR の側で定義してから足す（先取りを禁じた元の趣旨はここに残る）。
    private const string CurrentUserPlaceholder = "${current_user}";

    // FR-19, ADR-0036 D-03・D-06, ADR-0098 決定 1, [[IADR-0447]]: 主体の所属グループ ID の集合へ
    // **0..N 値に展開される**（`${current_user}` は 1 値）。供給元は IdP の所属照会であり、
    // トークンの `groups` クレームではない（`ADR-0088` 決定 1。`ScopeUserAttributeSource`）。
    private const string CurrentGroupsPlaceholder = "${current_groups}";

    // 利用者属性 × 文書属性フィルタを解決し、許可される属性条件を返す。
    //
    // FR-19, [[IADR-0447]] (#1447): `groups` は主体の所属グループ ID（`${current_groups}` の束縛値）。
    // **null と空集合を区別しない**（どちらも「所属が無い」）—— 区別すると、渡し忘れた呼び出しが
    // 「全グループに所属」へ倒れる余地が生まれる。
    public static AccessScopeResponse ResolveScope(
        AccessScopeRequest request, IEnumerable<AbacPolicy> policies, string action = PolicyAction.Read,
        IReadOnlySet<string>? groups = null)
    {
        var filters = new List<AttributeFilter>();
        // FR-19, ADR-0046 D-06 部品 3, IADR-0253 決定 1: read の選言（OR）を運ぶ名前つき分岐。
        // **マッチしたポリシー 1 件が 1 分岐**（その DocumentConditions が 1 本の連言）。
        var branches = new List<AccessScopeBranch>();
        // FR-05: deny-by-default。利用者にマッチするポリシーが 1 つも無ければアクセス不可。
        var granted = false;

        // 各ポリシーを評価し、利用者条件を満たすポリシーの文書条件を集約
        foreach (var policy in policies.Where(p => p.IsActive && p.Action == action))
        {
            if (!MatchesUserConditions(request.UserAttributes, policy.UserConditions))
                continue;

            // マッチしたポリシーが存在する＝アクセスを許可する根拠がある
            granted = true;

            // IADR-0253 決定 1: 分岐を 1 本足す。**分岐名はポリシー名である。**
            // 固定語彙（attribute / owner / shared）へ分類しないのは、ポリシーが FR-09 / UC-05 で
            // 管理者が定義するものであり、**どの語へ落とすかを実装が推測することになる**ためである
            // （計画も IADR も分類規則を定めていない）。ポリシー名なら SC-09 の表示と一致し、
            // 「**どのポリシーで見えたか**」をそのまま言える。
            //
            // IADR-0253 決定 3: 束縛はここでのみ解決する。述語側はプレースホルダを解釈しない。
            var boundFilters = (policy.DocumentConditions ?? [])
                .Select(kv => new AttributeFilter(kv.Key, BindPlaceholders(kv.Value, request.UserId, groups)))
                .ToList();

            // 🔴 FR-19, [[IADR-0447]] (#1447): **展開後に許可値が空になるフィルタを持つ分岐は、
            // 分岐ごと落とす。** `${current_groups}` は 0..N 値へ展開されるため、所属が無い主体では
            // `shared_with ∈ {}` という形が生まれる。空の許可集合は「何にも一致しない」であって
            // 「無条件」ではない —— ところが**分岐のフィルタが空**は本評価器の契約で
            // 「そのポリシーの範囲で全件許可」を意味する（`BffScopeResolver` / `AbacPageFilter` の
            // いずれもそう読む）。したがって空値のフィルタを残すと、消費側が**キーごと落として
            // 無条件許可へ倒す**余地ができる。**落とすのはフィルタではなく分岐である**
            // （フィルタだけ落とすと、連言の残りが緩い許可として立つ）。
            //
            // なお `granted` は true のままである（「マッチするポリシーは在った」は事実である）。
            // 分岐が 0 本になった場合、消費側は据え置きの `AllowedFilters`（リテラルのまま）で
            // 評価し、どの文書にも一致しない ＝ **deny 側へ倒れる**（下の 🔴 と同じ機構）。
            if (boundFilters.All(f => f.AllowedValues.Count > 0))
                branches.Add(new AccessScopeBranch(policy.Name, boundFilters));

            foreach (var (key, values) in policy.DocumentConditions ?? [])
            {
                // 🔴 **AllowedFilters は算出アルゴリズムごと据え置く**（IADR-0253 決定 2）。
                // **ここでは束縛しない。** 束縛すると未移行の消費側が
                // `owner ∈ {u1}` AND `confidentiality ∈ {internal}` という**壊れた連言**で判定し、
                // 「自分が所有する internal 文書」しか見えなくなる。リテラルのまま残せば
                // どの文書にも一致せず **deny 側へ倒れる**（作業仕様書 §4）。
                var existing = filters.FirstOrDefault(f => f.Key == key);
                if (existing is null)
                    filters.Add(new AttributeFilter(key, values));
                else
                    // 複数ポリシーがマッチした場合は union（ORで拡張）
                    filters[filters.IndexOf(existing)] = existing with
                    {
                        AllowedValues = existing.AllowedValues.Union(values).Distinct().ToList()
                    };
            }
        }

        return new AccessScopeResponse(request.UserId, filters, granted, branches);
    }

    // IADR-0253 決定 3: 分岐の中でだけプレースホルダを主体へ束縛する。
    // **既知のプレースホルダ以外はそのまま残す** —— 実装が語彙を勝手に増やさないため。
    //
    // FR-19, ADR-0036 D-03, ADR-0098 決定 1, [[IADR-0447]] (#1447):
    //   `${current_user}`   → 1 値（利用者識別子）
    //   `${current_groups}` → **0..N 値**（所属グループ ID へ展開。所属が無ければ 1 つも生えない）
    // 🔴 **同じ値を二重に足さない**（リテラルと束縛値が一致する場合。許可集合は集合である）。
    private static List<string> BindPlaceholders(
        List<string> values, string userId, IReadOnlySet<string>? groups)
    {
        var bound = new List<string>(values.Count);
        foreach (var value in values)
        {
            if (value == CurrentUserPlaceholder)
            {
                Append(bound, userId);
                continue;
            }
            if (value == CurrentGroupsPlaceholder)
            {
                if (groups is not null)
                    foreach (var group in groups) Append(bound, group);
                continue;
            }
            Append(bound, value);
        }
        return bound;

        static void Append(List<string> sink, string value)
        {
            if (!sink.Contains(value, StringComparer.Ordinal)) sink.Add(value);
        }
    }

    private static bool MatchesUserConditions(
        Dictionary<string, string> userAttrs, Dictionary<string, List<string>>? conditions)
    {
        // FR-05: 条件 null（＝条件なし）は全利用者にマッチ。null を foreach して落ちないよう防御する。
        // **この規則は #1324 が両方向の変異で固定している**（AbacEvaluatorTests の対）。
        foreach (var (key, allowedValues) in conditions ?? [])
        {
            // ADR-0080 決定 3: **属性を持たない場合はマッチしない**（フィルタ間は AND）。
            if (!userAttrs.TryGetValue(key, out var userValue))
                return false;

            // ADR-0080 決定 2 (#1323): **集合値の利用者属性は「交差が空でないこと」でマッチする。**
            // 単値キーは従来どおり値そのものの一致である —— 🔴 **一律に分割してはならない**。
            // `clearance` を区切り文字で割ると辞書外の値が「要素」として通り得る（IADR-0385 の禁則）。
            // 集合値キーの判定は `UserAttributeEncoding` が持つ分割規則へ委ねる（唯一の規則）。
            //
            // 🔴 部分集合ではなく交差である。ADR-0080 決定 2 は「タグを 1 つ足しただけで既存の
            // アクセスが失われる」振る舞いを明示的に退けている。ADR-0062 決定 2 の部分集合判定は
            // **属性割当の統制**であってアクセス判定ではない（向きが逆である）。
            var matched = UserAttributeEncoding.IsSetValued(key)
                ? UserAttributeEncoding.Split(userValue).Overlaps(allowedValues)
                : allowedValues.Contains(userValue, StringComparer.OrdinalIgnoreCase);

            if (!matched)
                return false;
        }
        return true;
    }
}
