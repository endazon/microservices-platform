using LlmGateway.Domain.Routing;

namespace LlmGateway.Common.Observability;

// FR-10, FR-11, NFR, ADR-0006, ADR-0044 決定 1 (#443): メトリクス属性値の正規化。
//
// **IADR-0110 が確立した「属性値の値域を閉じる」規律を、費用系の計器でもそのまま使うためにここへ出した。**
// 用途別・モデル別の費用は終了理由カウンタと**同じ軸**で読めなければ意味が無い（振り分けの前後を
// 同じ軸で比較して初めて効果が測れる。ADR-0044 §理由）。正規化を各計器へ複写すると、片方だけ
// 値域がずれて系列が合わなくなる。
internal static class LlmMetricValues
{
    // 未知値の集約先と「該当なし」。
    public const string Other = "other";
    public const string None = "none";

    // エンドポイントが purpose 未指定時に補う値（CompletionEndpoints の `?? "default"`）。
    // 設定に依存せず**常に**既知として扱う（設定から消えた場合に「呼び出し側は何も指定していないのに
    // other が増える」誤検知になるため）。
    public const string DefaultPurpose = "default";

    // purpose は呼び出し側が自由に指定できるため、設定（PurposeModels）で値域を閉じる。
    public static string NormalizePurpose(LlmRoutingOptions routing, string purpose)
    {
        if (string.Equals(purpose, DefaultPurpose, StringComparison.OrdinalIgnoreCase))
            return DefaultPurpose;
        return routing.PurposeModels.ContainsKey(purpose) ? purpose.ToLowerInvariant() : Other;
    }

    public static string Or(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;
}
