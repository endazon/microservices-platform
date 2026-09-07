using Platform.Shared.Contracts.Dtos;

namespace AuthorizationService.Domain;

// FR-05, ADR-0004: ABAC ポリシー評価エンジン（deny by default）
public class AbacEvaluator
{
    // FR-19, ADR-0036, IADR-0253 決定 3: 認可判定に使える束縛変数。
    // **1 つだけである。増やさない** —— 計画が束縛変数の語彙を定めていないため、実装が先取りしない。
    private const string CurrentUserPlaceholder = "${current_user}";

    // 利用者属性 × 文書属性フィルタを解決し、許可される属性条件を返す
    public static AccessScopeResponse ResolveScope(
        AccessScopeRequest request, IEnumerable<AbacPolicy> policies, string action = PolicyAction.Read)
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
            branches.Add(new AccessScopeBranch(
                policy.Name,
                (policy.DocumentConditions ?? [])
                    .Select(kv => new AttributeFilter(kv.Key, BindPlaceholders(kv.Value, request.UserId)))
                    .ToList()));

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

    // IADR-0253 決定 3: 分岐の中でだけ ${current_user} を主体へ束縛する。
    // **既知のプレースホルダ以外はそのまま残す** —— 実装が語彙を勝手に増やさないため。
    private static List<string> BindPlaceholders(List<string> values, string userId) =>
        values.Select(v => v == CurrentUserPlaceholder ? userId : v).ToList();

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
