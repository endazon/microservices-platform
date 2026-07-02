namespace LlmGateway.Api.Routing;

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
    // 未指定は組織既定の Internal、未知の値は安全側で Restricted に倒す（既定は安全側の原則）。
    public static SensitivityClass Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" => SensitivityClass.Internal,
        "public" => SensitivityClass.Public,
        "internal" => SensitivityClass.Internal,
        "confidential" => SensitivityClass.Confidential,
        "restricted" => SensitivityClass.Restricted,
        _ => SensitivityClass.Restricted
    };

    // 複数文書のうち最も高い機密区分を求める（ゲートウェイは入力の最高区分で判定する）。
    // 空集合は Public（送信対象の文書が無い）とみなす。
    public static SensitivityClass Highest(IEnumerable<string?> values)
        => values.Select(Parse).DefaultIfEmpty(SensitivityClass.Public).Max();
}
