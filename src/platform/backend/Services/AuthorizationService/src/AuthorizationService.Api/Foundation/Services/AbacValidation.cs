using AuthorizationService.Api.Foundation.Domain;

namespace AuthorizationService.Api.Foundation.Services;

// FR-09, UC-05, ADR-0004: 属性辞書・ポリシー・文書属性のバリデーション。
// UC-05 例外フロー「矛盾するポリシーは保存前に検証し、エラーを返す」を写像する。
// 段階導入方針: 属性辞書に「定義済みのキー」のみ許可値整合を検証し、未定義キー（自由タグ等）は許容する。
public static class AbacValidation
{
    // 属性辞書エントリの検証。update 時は excludeId で自分自身を一意チェックから除外する。
    public static List<string> ValidateAttributeDefinition(
        string? key, string? label, List<string>? allowedValues, string? scope,
        IEnumerable<AttributeDefinition> existing, Guid? excludeId = null)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(key))
            errors.Add("key は必須です。");
        if (string.IsNullOrWhiteSpace(label))
            errors.Add("label は必須です。");

        var normalizedScope = string.IsNullOrWhiteSpace(scope) ? AttributeScope.Document : scope;
        if (!AttributeScope.IsValid(normalizedScope))
            errors.Add($"scope は {string.Join(" / ", AttributeScope.All)} のいずれかである必要があります。");

        if (allowedValues is null || allowedValues.Count == 0)
        {
            errors.Add("allowedValues は 1 件以上必要です。");
        }
        else
        {
            if (allowedValues.Any(string.IsNullOrWhiteSpace))
                errors.Add("allowedValues に空の値を含めることはできません。");
            var dup = allowedValues
                .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
                .Where(grp => grp.Count() > 1)
                .Select(grp => grp.Key)
                .ToList();
            if (dup.Count > 0)
                errors.Add($"allowedValues に重複があります: {string.Join(", ", dup)}");
        }

        // 同一スコープ内でキーは一意（辞書としての整合）。
        if (!string.IsNullOrWhiteSpace(key)
            && existing.Any(a => a.Id != excludeId
                && string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Scope, normalizedScope, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"key '{key}' は scope '{normalizedScope}' に既に定義済みです。");
        }

        return errors;
    }

    // ABAC ポリシーの検証。定義済みキーのみ許可値整合を検証する（段階導入）。
    public static List<string> ValidatePolicy(
        string? name, string? action,
        Dictionary<string, List<string>>? userConditions,
        Dictionary<string, List<string>>? documentConditions,
        IEnumerable<AttributeDefinition> definitions)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(name))
            errors.Add("name は必須です。");

        if (string.IsNullOrWhiteSpace(action) || !PolicyAction.IsValid(action))
            errors.Add($"action は {string.Join(" / ", PolicyAction.All)} のいずれかである必要があります。");

        var defList = definitions.ToList();
        ValidateConditions("userConditions", userConditions, defList, AttributeScope.User, errors);
        ValidateConditions("documentConditions", documentConditions, defList, AttributeScope.Document, errors);

        return errors;
    }

    private static void ValidateConditions(
        string field, Dictionary<string, List<string>>? conditions,
        IReadOnlyCollection<AttributeDefinition> definitions, string scope, List<string> errors)
    {
        if (conditions is null)
            return;

        foreach (var (key, values) in conditions)
        {
            if (values is null || values.Count == 0)
            {
                errors.Add($"{field}.{key} の値集合は空にできません。");
                continue;
            }

            // 辞書に定義済みのキーのみ許可値整合を検証（未定義キーは許容）。
            var def = definitions.FirstOrDefault(d =>
                string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.Scope, scope, StringComparison.OrdinalIgnoreCase));
            if (def is null)
                continue;

            var invalid = values
                .Where(v => !def.AllowedValues.Contains(v, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (invalid.Count > 0)
                errors.Add($"{field}.{key} に辞書外の値があります: {string.Join(", ", invalid)}");
        }
    }

    // FR-09, IADR-0006: 指定ポリシーが当該属性キー（scope 一致）を条件に参照しているか。
    // 属性辞書の削除可否判定に用いる（参照中の削除は制約緩みを招くため拒否する）。
    public static bool PolicyReferencesAttribute(AbacPolicy policy, string key, string scope)
    {
        var conditions = string.Equals(scope, AttributeScope.User, StringComparison.OrdinalIgnoreCase)
            ? policy.UserConditions
            : policy.DocumentConditions;
        return conditions is not null
            && conditions.Keys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
    }

    // 文書に付与する属性値が辞書と整合するか検証する（必須充足＋許可値整合）。
    public static List<string> ValidateDocumentAttributes(
        Dictionary<string, string>? attributes,
        IEnumerable<AttributeDefinition> definitions)
    {
        var errors = new List<string>();
        var attrs = attributes ?? new Dictionary<string, string>();
        var docDefs = definitions
            .Where(d => string.Equals(d.Scope, AttributeScope.Document, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 必須属性の充足。
        foreach (var def in docDefs.Where(d => d.Required))
        {
            if (!attrs.TryGetValue(def.Key, out var value) || string.IsNullOrWhiteSpace(value))
                errors.Add($"必須属性 '{def.Key}' が未設定です。");
        }

        // 許可値整合（定義済みキーのみ検証、未定義キーは自由タグとして許容）。
        foreach (var (key, value) in attrs)
        {
            var def = docDefs.FirstOrDefault(d =>
                string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
            if (def is null)
                continue;
            if (!def.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
                errors.Add($"属性 '{key}' の値 '{value}' は許可値に含まれません。");
        }

        return errors;
    }
}
