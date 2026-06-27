using AuthorizationService.Api.Domain;
using KnowledgePlatform.Shared.Contracts.Dtos;

namespace AuthorizationService.Api.Services;

// FR-05, ADR-0004: ABAC ポリシー評価エンジン（deny by default）
public class AbacEvaluator
{
    // 利用者属性 × 文書属性フィルタを解決し、許可される属性条件を返す
    public static AccessScopeResponse ResolveScope(
        AccessScopeRequest request, IEnumerable<AbacPolicy> policies, string action = PolicyAction.Read)
    {
        var filters = new List<AttributeFilter>();
        // FR-05: deny-by-default。利用者にマッチするポリシーが 1 つも無ければアクセス不可。
        var granted = false;

        // 各ポリシーを評価し、利用者条件を満たすポリシーの文書条件を集約
        foreach (var policy in policies.Where(p => p.IsActive && p.Action == action))
        {
            if (!MatchesUserConditions(request.UserAttributes, policy.UserConditions))
                continue;

            // マッチしたポリシーが存在する＝アクセスを許可する根拠がある
            granted = true;

            foreach (var (key, values) in policy.DocumentConditions)
            {
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

        return new AccessScopeResponse(request.UserId, filters, granted);
    }

    private static bool MatchesUserConditions(
        Dictionary<string, string> userAttrs, Dictionary<string, List<string>> conditions)
    {
        foreach (var (key, allowedValues) in conditions)
        {
            if (!userAttrs.TryGetValue(key, out var userValue))
                return false;
            if (!allowedValues.Contains(userValue, StringComparer.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}
