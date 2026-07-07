using Microsoft.Extensions.Options;

namespace LlmGateway.Api.Foundation.Routing;

// FR-02, ADR-0016, ADR-0017（Issue #98 レビュー対応）: 埋め込みルーティング設定を起動時に検証する。
//
// 背景: 配備（docker-compose / Helm）はセルフホスト有効化を配列インデックス依存の環境変数
// （例 `Embedding__Routing__Endpoints__1__Enabled`）で上書きする。将来エンドポイントの追加・並び替えで
// インデックスがずれると、意図せず別エンドポイント（例 Voyage 側）を無効化し得るが、これは
// コンパイル時に検知できない。そこで起動時アサーション（ValidateOnStart）で不整合を fail-fast する。
public sealed class EmbeddingRoutingOptionsValidator : IValidateOptions<EmbeddingRoutingOptions>
{
    // Tier ごとに期待するプロバイダキー（keyed DI）。ティアとプロバイダの取り違え（並び替え・誤設定）を検知する。
    private static readonly Dictionary<ProtectionTier, string> ExpectedProviderByTier = new()
    {
        [ProtectionTier.A] = "selfhosted-embedding", // セルフホスト（社外送信なし）
        [ProtectionTier.B] = "voyage",               // 保護契約済み外部
    };

    public ValidateOptionsResult Validate(string? name, EmbeddingRoutingOptions options)
    {
        var errors = new List<string>();

        if (options.Endpoints.Count == 0)
        {
            errors.Add("Embedding:Routing:Endpoints が空です。少なくとも既定（Voyage/ティアB）を定義してください。");
            return ValidateOptionsResult.Fail(errors);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (e, i) in options.Endpoints.Select((e, i) => (e, i)))
        {
            var id = string.IsNullOrWhiteSpace(e.Name) ? $"index {i}" : e.Name;

            if (string.IsNullOrWhiteSpace(e.Name)) errors.Add($"Endpoints[{i}].Name が未設定です。");
            else if (!names.Add(e.Name)) errors.Add($"Endpoints[{i}].Name '{e.Name}' が重複しています。");

            if (string.IsNullOrWhiteSpace(e.Provider)) errors.Add($"Endpoint '{id}' の Provider が未設定です。");
            if (string.IsNullOrWhiteSpace(e.Model)) errors.Add($"Endpoint '{id}' の Model が未設定です。");
            if (string.IsNullOrWhiteSpace(e.Collection)) errors.Add($"Endpoint '{id}' の Collection が未設定です。");
            if (e.Dimensions <= 0) errors.Add($"Endpoint '{id}' の Dimensions は正の値が必要です（現在 {e.Dimensions}）。");

            // ティア↔プロバイダの取り違え（並び替え・誤設定）を検知する。
            if (ExpectedProviderByTier.TryGetValue(e.Tier, out var expected)
                && !string.IsNullOrWhiteSpace(e.Provider)
                && !string.Equals(e.Provider, expected, StringComparison.Ordinal))
            {
                errors.Add(
                    $"Endpoint '{id}' はティア{e.Tier} だが Provider が '{e.Provider}'（期待 '{expected}'）です。" +
                    "エンドポイントの並び替え・インデックス依存の上書きで取り違えが起きていないか確認してください。");
            }
        }

        // 既定（public）/ 検索クエリの送信先が 1 つも有効でない状態を fail-fast する。
        // 例: インデックス依存の環境変数で誤って Voyage（ティアB）を無効化し、セルフホスト（ティアA）も
        // 既定無効のまま → 全 public 取り込みと検索クエリが黙って fail-closed する事故を起動時に検知する。
        var publicTiers = EmbeddingEgress.AllowedTiers(SensitivityClass.Public);
        var hasEnabledDefaultPath = options.Endpoints.Any(e => e.Enabled && publicTiers.Contains(e.Tier));
        if (!hasEnabledDefaultPath)
        {
            errors.Add(
                "public/internal・検索クエリを送信できる有効なエンドポイントがありません" +
                $"（許容ティア {string.Join(",", publicTiers.OrderBy(t => t))}）。" +
                "インデックス依存の Enabled 上書きで既定（Voyage）を誤って無効化していないか確認してください。");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
