using LlmGateway.Domain.Authorization;
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
    private const string EmbeddingPurpose = LlmCallLimiter.EmbeddingPurpose;

    public ValidateOptionsResult Validate(string? name, LlmCallLimitOptions options)
    {
        var errors = new List<string>();
        const string Section = LlmCallLimitOptions.SectionName;

        if (!Enum.IsDefined(options.Mode))
            errors.Add($"{Section}:Mode が不正です（{options.Mode}）。Report か Enforce を指定してください。");
        if (!Enum.IsDefined(options.EmbeddingMode))
            errors.Add($"{Section}:EmbeddingMode が不正です（{options.EmbeddingMode}）。Off・Report・Enforce のいずれかを指定してください。");
        if (options.MaxOutputTokens <= 0)
            errors.Add($"{Section}:MaxOutputTokens は正の値でなければなりません（{options.MaxOutputTokens}）。");
        if (options.MaxPromptChars <= 0)
            errors.Add($"{Section}:MaxPromptChars は正の値でなければなりません（{options.MaxPromptChars}）。");

        if (options.UnknownModelPrice.InputPerMillionTokens <= 0m || options.UnknownModelPrice.OutputPerMillionTokens <= 0m)
            errors.Add($"{Section}:UnknownModelPrice の単価は正の値でなければなりません（0 にすると単価の無いモデルで費用の上限が効かない）。");

        // NFR-18, IADR-0701: 埋め込みの計上単価も 0 以下にしない（0 だと埋め込みで費用の上限が黙って効かない）。
        foreach (var (model, price) in options.EmbeddingPrices)
            if (string.IsNullOrWhiteSpace(model) || price <= 0m)
                errors.Add($"{Section}:EmbeddingPrices:{model} の単価は正の値でなければなりません（{price}）。");

        // NFR-18, IADR-0692: 段 2（Enforce）で既定の当日の費用の上限が無いまま起動しない（fail-closed）。
        // 呼び手の項目は書かなかった値に Default を使うので、Default に金額があれば、どの呼び手にも金額が効く。
        // 無ければ費用の判定は黙って働かず、「強制した」と読める設定のまま費用が無制限になる。0 以下は下の検査が落とす。
        if (options.Mode == LlmCallLimitMode.Enforce && options.Default.DailyCostLimit is null)
            errors.Add($"{Section}:Mode が Enforce なのに {Section}:Default:DailyCostLimit が設定されていません。"
                + "段 2 では当日の費用の上限（全呼び手の既定）を正の値で設定してください（未設定のままでは費用が無制限になる）。");
        // NFR-18, IADR-0701: 埋め込みの段 2 も同じ（金額が無いと、埋め込みの費用の判定が黙って働かない）。
        if (options.EmbeddingMode == LlmGuardStage.Enforce && options.Default.DailyCostLimit is null)
            errors.Add($"{Section}:EmbeddingMode が Enforce なのに {Section}:Default:DailyCostLimit が設定されていません。"
                + "段 2 では当日の費用の上限（全呼び手の既定）を正の値で設定してください（未設定のままでは費用が無制限になる）。");

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
                     || string.Equals(purpose, EmbeddingPurpose, StringComparison.OrdinalIgnoreCase)
                     || purposes.ContainsKey(purpose);
            if (!known)
                errors.Add($"{path}:PurposeDailyCostLimits:{purpose} は既知の用途ではありません。"
                    + $"Llm:Routing:PurposeModels のキー・{DefaultPurpose}・{OtherPurpose}・{EmbeddingPurpose} のいずれかを指定してください。");
            if (limit <= 0m)
                errors.Add($"{path}:PurposeDailyCostLimits:{purpose} の金額が 0 以下です（{limit}）。");
        }
    }
}
