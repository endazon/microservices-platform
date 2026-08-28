
namespace McpServer.Domain;

// FR-16, ADR-0024 §5: 宣言的公開構成のスキーマ検証。
// 「検証を通らない構成は適用不可」であり、起動時に fail-fast させる。
public static class ToolPublicationConfigValidator
{
    // ADR-0024 §決定「初期公開範囲」・11_mcp-server-integration §6:
    // AI 分析系（`ai.*`）は初期公開に含めない。要約系（`get_cluster_summary`。LLM 呼び出しを伴う）
    // も公開しない。**構成に書けてしまうと、レビューを通り抜けた瞬間に公開される**ため、
    // 構成の検証で弾く。公開範囲を広げるには計画の後続 ADR が要る。
    private const string ForbiddenToolPrefix = "ai.";
    private const string ForbiddenToolSuffix = "get_cluster_summary";

    // 検証結果。Errors が空でなければ構成を適用しない。
    public static IReadOnlyList<string> Validate(ToolPublicationConfig config)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.Version))
            errors.Add("公開構成に version がありません。");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in config.Tools)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                errors.Add("公開構成のツール名が空です。");
                continue;
            }
            if (string.IsNullOrWhiteSpace(entry.Service))
                errors.Add($"ツール '{entry.Name}' に service がありません。");

            // ツール名（公開名）の一意性。
            var published = string.IsNullOrWhiteSpace(entry.PublishedName) ? entry.Name : entry.PublishedName;
            if (!seen.Add(published))
                errors.Add($"公開名 '{published}' が重複しています。");

            if (IsForbidden(entry.Name) || IsForbidden(published))
                errors.Add(
                    $"ツール '{entry.Name}' は初期公開範囲外です（AI 分析系・要約系は公開しない）。");
        }

        errors.AddRange(ValidateServiceAccountAttributes(config.ServiceAccountAttributes));
        return errors;
    }

    private static bool IsForbidden(string name)
        => name.StartsWith(ForbiddenToolPrefix, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(ForbiddenToolSuffix, StringComparison.OrdinalIgnoreCase);

    // 🔴 FR-16, ADR-0024（2026-08-02 注記）, ADR-0034 決定 9:
    // **サービスアカウントに個人資料を読ませる属性割当は構成上禁止**であり、スキーマ検証で弾く。
    // 同じ検証を管理 API（属性割当）でも使う（McpClientEndpoints）。単一の関数に閉じるのは、
    // 2 箇所へ書くと片方だけが緩む形になるためである。
    public static IReadOnlyList<string> ValidateServiceAccountAttributes(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? assignments)
    {
        if (assignments is null) return [];

        var errors = new List<string>();
        foreach (var (clientId, attributes) in assignments)
            errors.AddRange(ValidateServiceAccountAttributes(clientId, attributes));
        return errors;
    }

    public static IReadOnlyList<string> ValidateServiceAccountAttributes(
        string clientId, IReadOnlyDictionary<string, string> attributes)
    {
        if (DocumentScope.IsPrivateNote(attributes))
        {
            return
            [
                $"サービスアカウント '{clientId}' へ {DocumentScope.Key}={DocumentScope.PrivateNote} は"
                + "割り当てられません（個人資料を読ませる属性割当は構成上禁止）。"
            ];
        }
        return [];
    }
}
