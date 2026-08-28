using Microsoft.Extensions.Options;

namespace LlmGateway.Domain.Pricing;

// FR-10, NFR, ADR-0044 決定 3 (#443): 単価の解決結果。
// **「見つからなかった」を 0 円と区別する**ためだけに存在する型である。
public enum PricingStatus
{
    // 有効期間に該当する単価が見つかった。
    Priced,

    // モデルの単価は登録されているが、**どの区間にも該当しない**（期限切れ・未来の呼び出し）。
    OutOfEffectivePeriod,

    // そのモデルの単価が 1 件も登録されていない。
    NoEntryForModel,
}

// 解決結果。Status が Priced のときだけ Entry / Cost が意味を持つ。
public readonly record struct PriceLookupResult(PricingStatus Status, ModelPriceEntry? Entry, decimal Cost)
{
    public bool IsPriced => Status == PricingStatus.Priced;
}

// FR-10, NFR, ADR-0006, ADR-0044 決定 3 (#443): 単価表を読み、トークン数を金額へ換算する。
//
// **換算をここで行うことが決定の要点である。** 単価を Grafana のクエリやレコーディングルールへ書くと
// 式に単価が散り、**有効期間を実行時に評価する主体がどこにも存在しなくなる**（＝期限切れの警告を
// 出せる場所が無くなる）。設定を読む側と換算する側を同じにして初めて、期限切れが表に出る。
public sealed class ModelPriceTable(
    IOptionsMonitor<ModelPricingOptions> options,
    ILogger<ModelPriceTable> logger)
{
    // 単価表の通貨。
    public string Currency => options.CurrentValue.Currency;

    // 指定時刻に有効な単価を解決し、入力・出力トークンを金額へ換算する。
    //
    // 🔴 **該当が無い場合に 0 円を返さない。** ADR-0044 決定 3 は「期間外の単価で試算した場合は警告を出す。
    // どの単価も有効期間に該当しないモデルが現れた場合も同様に警告する。**無音で 0 円として扱ってはならない**」
    // と定める。0 を返すと、期限切れが**費用の減少**に化けて増加の検知をすり抜ける。
    public PriceLookupResult Estimate(string? model, long inputTokens, long outputTokens, DateTimeOffset at)
    {
        var table = options.CurrentValue.Models;
        if (string.IsNullOrWhiteSpace(model)
            || !table.TryGetValue(model, out var entries)
            || entries.Count == 0)
        {
            logger.LogWarning(
                "LLM pricing: no price entry is configured for model {Model}. Cost is NOT recorded for this "
                + "completion (ADR-0044 決定 3: 無音で 0 円として扱わない). Add Llm:Pricing:Models:{Model}.",
                model ?? "(none)", model ?? "(none)");
            return new PriceLookupResult(PricingStatus.NoEntryForModel, null, 0m);
        }

        var entry = entries.FirstOrDefault(e => e.Covers(at));
        if (entry is null)
        {
            logger.LogWarning(
                "LLM pricing: model {Model} has {EntryCount} price entries but none covers {At:o}. "
                + "The price table is likely stale (導入価格の終了日を反映し忘れると試算は静かに過小になる). "
                + "Cost is NOT recorded for this completion.",
                model, entries.Count, at);
            return new PriceLookupResult(PricingStatus.OutOfEffectivePeriod, null, 0m);
        }

        // 入力と出力は単価が異なるため別々に按分する（百万トークンあたりの単価）。
        const decimal PerMillion = 1_000_000m;
        var cost = (inputTokens / PerMillion * entry.InputPerMillionTokens)
                 + (outputTokens / PerMillion * entry.OutputPerMillionTokens);
        return new PriceLookupResult(PricingStatus.Priced, entry, cost);
    }
}
