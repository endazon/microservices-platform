namespace AuthorizationService.Domain;

// FR-05, FR-09, SC-17, 計画 ADR-0116 決定 1, ADR-0115 決定 3, [[IADR-0473]] (#1610):
// **利用者属性 `department` は SC-17 の属性の差し替えでは書かない**（部門は部門グループの所属で変え、属性は同期が追いつく）。
//
// ■ 2 点で閉じる。
//   1. 端点（`PUT /authz/users/{id}/attributes`）は要求に `department` があれば 400 で拒む（受け付けて無視しない）。
//   2. ポートの `ReplaceAttributesAsync` は要求側の `department` を採らず、**現在値を持ち越す**（保持起点と同じ形。[[IADR-0428]]）。
//      差し替えは全置換なので、持ち越さないと機密区分上限を 1 つ直しただけで部門が消える ＝ 属性を書くことになる。
// ■ キーの照合は大小文字無視（属性辞書のキーの一意性と同じ）。Keycloak に載るキーは `department` の 1 つである。
public static class DepartmentAttributes
{
    public const string Key = DepartmentAttributeReconciliation.AttributeKey;

    /// <summary>`department` のキーか（大小文字無視）。</summary>
    public static bool IsDepartment(string? key) => string.Equals(key, Key, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 差し替えの要求から `department` を落とし、現在の `department`（あれば）を持ち越す。
    /// </summary>
    public static IReadOnlyDictionary<string, string> PreserveDepartment(
        IReadOnlyDictionary<string, string> current, IReadOnlyDictionary<string, string> requested)
    {
        var merged = requested.Where(kv => !IsDepartment(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        foreach (var (key, value) in current.Where(kv => IsDepartment(kv.Key)))
            merged[key] = value;
        return merged;
    }
}
