using LlmGateway.Domain.Routing;
using Microsoft.Extensions.Options;

namespace LlmGateway.Domain.Limits;

// NFR-18, IADR-0678 (#2404): 送る前の上限の設定を起動時に検証する（ValidateOnStart）。
//
// 0 以下の上限は「常に超過」になり、段 2 では全件を止める。用途の綴り違いは「上限を設定した」と
// 読めるのに 1 件も数えない。**どちらも配備の失敗として表に出す**（LlmBudgetOptionsValidator と同じ判断）。
public sealed class LlmCallLimitOptionsValidator(IOptionsMonitor<LlmRoutingOptions> routing)
    : IValidateOptions<LlmCallLimitOptions>
{
    private const string DefaultPurpose = "default";
    private const string OtherPurpose = "other";

    public ValidateOptionsResult Validate(string? name, LlmCallLimitOptions options)
    {
        var errors = new List<string>();
        const string Section = LlmCallLimitOptions.SectionName;

        if (!Enum.IsDefined(options.Mode))
            errors.Add($"{Section}:Mode が不正です（{options.Mode}）。Report か Enforce を指定してください。");
        if (options.MaxOutputTokens <= 0)
            errors.Add($"{Section}:MaxOutputTokens は正の値でなければなりません（{options.MaxOutputTokens}）。");
        if (options.MaxPromptChars <= 0)
            errors.Add($"{Section}:MaxPromptChars は正の値でなければなりません（{options.MaxPromptChars}）。");

        if (options.UnknownModelPrice.InputPerMillionTokens <= 0m || options.UnknownModelPrice.OutputPerMillionTokens <= 0m)
            errors.Add($"{Section}:UnknownModelPrice の単価は正の値でなければなりません（0 にすると単価の無いモデルで費用の上限が効かない）。");

        ValidateCaller($"{Section}:Default", options.Default, errors);
        foreach (var (caller, limits) in options.Callers)
        {
            if (string.IsNullOrWhiteSpace(caller))
                errors.Add($"{Section}:Callers に空の呼び手の名前があります。");
            ValidateCaller($"{Section}:Callers:{caller}", limits, errors);
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private void ValidateCaller(string path, LlmCallerLimits limits, List<string> errors)
    {
        if (limits.RequestsPerMinute is <= 0)
            errors.Add($"{path}:RequestsPerMinute は正の値でなければなりません（{limits.RequestsPerMinute}）。上限を外すなら項目ごと削除してください。");
        if (limits.PurposeRequestsPerMinute is <= 0)
            errors.Add($"{path}:PurposeRequestsPerMinute は正の値でなければなりません（{limits.PurposeRequestsPerMinute}）。");
        if (limits.DailyCostLimit is <= 0m)
            errors.Add($"{path}:DailyCostLimit は正の値でなければなりません（{limits.DailyCostLimit}）。");

        var purposes = routing.CurrentValue.PurposeModels;
        foreach (var (purpose, limit) in limits.PurposeDailyCostLimits)
        {
            var known = string.Equals(purpose, DefaultPurpose, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(purpose, OtherPurpose, StringComparison.OrdinalIgnoreCase)
                     || purposes.ContainsKey(purpose);
            if (!known)
                errors.Add($"{path}:PurposeDailyCostLimits:{purpose} は既知の用途ではありません。"
                    + $"Llm:Routing:PurposeModels のキー・{DefaultPurpose}・{OtherPurpose} のいずれかを指定してください。");
            if (limit <= 0m)
                errors.Add($"{path}:PurposeDailyCostLimits:{purpose} の金額が 0 以下です（{limit}）。");
        }
    }
}
