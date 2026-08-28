using Microsoft.Extensions.Options;

namespace LlmGateway.Domain.Pricing;

// FR-10, NFR, ADR-0044 決定 3 (#443): 単価表を起動時に検証する（ValidateOnStart）。
//
// **区間の重なりを実行時に黙って先勝ちで解決しない。** 重なったまま動かすと、同じ呼び出しが
// 「どちらの単価で換算されたか」を後から特定できず、費用の突合が成り立たない。
// 単価表の誤りは**配備の失敗として表に出す**のが安全側である（誤った金額を出し続けるより良い）。
public sealed class ModelPricingOptionsValidator : IValidateOptions<ModelPricingOptions>
{
    public ValidateOptionsResult Validate(string? name, ModelPricingOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Currency))
            errors.Add("Llm:Pricing:Currency が未設定です。");

        foreach (var (model, entries) in options.Models)
        {
            if (entries.Count == 0)
            {
                errors.Add($"Llm:Pricing:Models:{model} が空です。単価を 1 件以上定義するか、項目ごと削除してください。");
                continue;
            }

            foreach (var (entry, i) in entries.Select((e, i) => (e, i)))
            {
                if (entry.InputPerMillionTokens < 0 || entry.OutputPerMillionTokens < 0)
                    errors.Add($"{model}[{i}] の単価が負値です（入力 {entry.InputPerMillionTokens} / 出力 {entry.OutputPerMillionTokens}）。");

                if (entry.EffectiveFrom is { } from && entry.EffectiveTo is { } to && from >= to)
                    errors.Add($"{model}[{i}] の有効期間が空です（EffectiveFrom {from:o} >= EffectiveTo {to:o}）。区間は [From, To) です。");
            }

            // 区間の重なり検出。半開区間 [From, To) は「一方の開始が他方の終了と等しい」場合に重ならない。
            for (var i = 0; i < entries.Count; i++)
            {
                for (var j = i + 1; j < entries.Count; j++)
                {
                    if (Overlaps(entries[i], entries[j]))
                        errors.Add($"{model} の単価区間 [{i}] と [{j}] が重なっています。"
                            + "重なりを実行時に先勝ちで解決すると、どちらの単価で換算したか追跡できません。");
                }
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    // 半開区間 [From, To) 同士の重なり判定。省略は無限（過去方向 / 未来方向）とみなす。
    private static bool Overlaps(ModelPriceEntry a, ModelPriceEntry b)
    {
        var aFrom = a.EffectiveFrom ?? DateTimeOffset.MinValue;
        var aTo = a.EffectiveTo ?? DateTimeOffset.MaxValue;
        var bFrom = b.EffectiveFrom ?? DateTimeOffset.MinValue;
        var bTo = b.EffectiveTo ?? DateTimeOffset.MaxValue;
        return aFrom < bTo && bFrom < aTo;
    }
}
