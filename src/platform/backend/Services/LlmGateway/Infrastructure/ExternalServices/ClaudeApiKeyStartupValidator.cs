using LlmGateway.Domain.Routing;
using Microsoft.Extensions.Options;

namespace LlmGateway.Infrastructure.ExternalServices;

// FR-11, ADR-0010, IADR-0641 (#2136): Claude（ティアB）のエンドポイントが有効なのに API キー `Llm:ApiKey` が空なら、
// 起動時に止める（ValidateOnStart）。
//
// 背景: AnthropicClient は遅延シングルトンで、空のキーは最初の Claude 呼び出しの 401 で初めて分かっていた
// （readiness は通ったまま）。Helm は Secret の欠落では Pod を作らないが、値が空の Secret は通す。
//
// 鍵なしで動かす検証スタック（compose・values-local・dotnet run）は `Llm:AllowMissingApiKey=true` を明示する。
// 値が true（大文字小文字は問わない）のときだけ効く（置いていない・true 以外は opt-out にしない＝fail-closed）。
// 大文字小文字を問わないのは、JSON の真偽値 true が構成では "True" として読まれるためである（appsettings.Development.json）。
// Claude のエンドポイントがすべて無効なら、鍵を使う経路が無いので検証しない。
//
// 🔴 鍵の値は文言に差し込まない（設定キーの名前とエンドポイント名だけ）。
public sealed class ClaudeApiKeyStartupValidator(IConfiguration configuration) : IValidateOptions<LlmRoutingOptions>
{
    public const string AllowMissingConfigKey = "Llm:AllowMissingApiKey";

    // keyed DI のプロバイダキー（Program.cs の AddKeyedSingleton<ILlmProvider, ClaudeProvider>("claude")）。
    public const string ClaudeProvider = "claude";

    public ValidateOptionsResult Validate(string? name, LlmRoutingOptions options)
    {
        var enabledClaude = EnabledClaudeEndpoints(options);
        if (enabledClaude.Count == 0 || !IsKeyMissing(configuration) || IsMissingKeyAllowed(configuration))
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(
            $"Claude のエンドポイント（{string.Join(", ", enabledClaude)}）が有効だが、設定 {AnthropicClientFactory.ApiKeyConfigKey}"
            + "（env Llm__ApiKey）が空である。このまま起動すると、最初の Claude 呼び出しが 401 で失敗する。"
            + $"鍵を供給するか、鍵なしで動かす検証スタックでは {AllowMissingConfigKey}=true（env Llm__AllowMissingApiKey）を明示する"
            + "（#2136 / IADR-0641）。");
    }

    public static IReadOnlyList<string> EnabledClaudeEndpoints(LlmRoutingOptions options) =>
        [.. options.Endpoints
            .Where(e => e.Enabled && string.Equals(e.Provider, ClaudeProvider, StringComparison.Ordinal))
            .Select(e => e.Name)];

    public static bool IsKeyMissing(IConfiguration configuration) =>
        string.IsNullOrWhiteSpace(configuration[AnthropicClientFactory.ApiKeyConfigKey]);

    public static bool IsMissingKeyAllowed(IConfiguration configuration) =>
        string.Equals(configuration[AllowMissingConfigKey], "true", StringComparison.OrdinalIgnoreCase);
}
