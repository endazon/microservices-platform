namespace LlmGateway.Domain.Routing;

// FR-11, ADR-0010, 08_data-egress-policy: 文書の機密区分（ABAC confidentiality）。
// 値が大きいほど機密度が高い（越境の制約が強い）。
public enum SensitivityClass
{
    Public = 0,
    Internal = 1,
    Confidential = 2,
    Restricted = 3
}

public static class SensitivityClasses
{
    // 文字列（ABAC の confidentiality 値）を機密区分へ写像する。
    // 08_data-egress-policy「既定は安全側」/ FR-05 deny-by-default に従い、
    // 未指定（null/空）・未知の値はいずれも安全側の Restricted に倒す。
    public static SensitivityClass Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "public" => SensitivityClass.Public,
        "internal" => SensitivityClass.Internal,
        "confidential" => SensitivityClass.Confidential,
        "restricted" => SensitivityClass.Restricted,
        // null / 空文字 / 未知の値はすべて安全側（Restricted）へ倒す。
        _ => SensitivityClass.Restricted
    };

    // 複数文書のうち最も高い機密区分を求める（ゲートウェイは入力の最高区分で判定する）。
    // 空集合は Public（送信対象の文書が無い）とみなす。
    public static SensitivityClass Highest(IEnumerable<string?> values)
        => values.Select(Parse).DefaultIfEmpty(SensitivityClass.Public).Max();
}
