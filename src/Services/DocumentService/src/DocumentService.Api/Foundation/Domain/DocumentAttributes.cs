namespace DocumentService.Api.Foundation.Domain;

// FR-05, FR-06, UC-03, SC-05, IADR-0047: 文書属性の必須検証（最終防衛線）。
// 機密区分（confidentiality）は SC-05 / UC-03 例外フローで必須。フロントの select 既定値に依存せず、
// サービス側で欠落・未知値を拒否する（BFF 迂回の直接呼び出しでも実効化する。IADR-0044 の多層防御と整合）。
public static class DocumentAttributes
{
    // FR-05: 機密区分の属性キー。
    public const string ConfidentialityKey = "confidentiality";

    // FR-05: 機密区分の正準値集合。AuthorizationService の AttributeDefinition.AllowedValues と一致させる
    // （["public","internal","confidential","restricted"]）。動的な属性辞書照合は IADR-0047 で見送り、
    // ここでは静的な正準集合で検証する。
    public static readonly IReadOnlySet<string> AllowedConfidentiality =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "public",
            "internal",
            "confidential",
            "restricted",
        };

    // UC-03 例外フロー / SC-05: 機密区分が付与され、正準値であることを検証する。
    // 未指定・欠落・未知値はいずれも保存拒否（400）とする。
    public static (bool Ok, string? Error) ValidateConfidentiality(
        IReadOnlyDictionary<string, string>? attributes)
    {
        if (attributes is null ||
            !attributes.TryGetValue(ConfidentialityKey, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            return (false, "機密区分（confidentiality）は必須です。");
        }

        if (!AllowedConfidentiality.Contains(value))
        {
            return (false,
                $"機密区分（confidentiality）の値 '{value}' は不正です。" +
                $"許容値: {string.Join(" / ", AllowedConfidentiality)}。");
        }

        return (true, null);
    }
}
