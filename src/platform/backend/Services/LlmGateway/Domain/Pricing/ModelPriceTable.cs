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

    // FR-10, IADR-0671 (#2396): 単価の区間は見つかったが、要求にキャッシュの書き・読みがあるのに、その段の書き・読みの単価が無い。
    // 入力単価で代わりに数えない（書きは過小・読みは過大になる）。計器の属性は no_entry（「必要な単価が未登録」）へ寄せる。
    NoCacheRate,
}

// 解決結果。Status が Priced のときだけ Cost が意味を持つ（NoCacheRate は Entry / LongPromptApplied も持つ）。
// LongPromptApplied: プロンプト長の上段（ModelPriceEntry.LongPrompt）で換算したか（IADR-0531）。
public readonly record struct PriceLookupResult(
    PricingStatus Status, ModelPriceEntry? Entry, decimal Cost, bool LongPromptApplied = false)
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
    public PriceLookupResult Estimate(
        string? model, long inputTokens, long outputTokens, DateTimeOffset at, PromptCacheTokens cache = default)
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
        // FR-11, IADR-0531 (#1875): プロンプト長で単価が 2 段のモデル（claude-haiku-5-5）は、**その要求のプロンプト全体**で
        // 段を選ぶ（要求ごとに決まる。集計期間の合計で選ばない）。
        // ［2026-11-05 / #2396・IADR-0671］ゲートウェイはプロンプトキャッシュを使うようになった。inputTokens はプロンプト全体
        // （キャッシュの書き・読みを含む。提供元の段の判定量と同じ）であり、cache はその**うち**の書き・読みである。
        // 金額 = 残り × 入力単価 ＋ 書き × 書き単価 ＋ 読み × 読み単価 ＋ 出力 × 出力単価。
        const decimal PerMillion = 1_000_000m;
        var rates = entry.RatesFor(inputTokens);
        if (!cache.IsEmpty
            && ((cache.Write > 0 && rates.CacheWritePerMillion is null) || (cache.Read > 0 && rates.CacheReadPerMillion is null)))
        {
            // 🔴 入力単価で代わりに数えない（書きは 1.25 倍の過小・読みは 10〜20 倍の過大）。0 円にもしない（ADR-0044 決定 3）。
            logger.LogWarning(
                "LLM pricing: model {Model} reported prompt-cache tokens (write {CacheWrite}, read {CacheRead}) but the price entry "
                + "has no cache write/read rate{Tier}. Cost is NOT recorded for this completion. "
                + "Add CacheWritePerMillionTokens / CacheReadPerMillionTokens to Llm:Pricing:Models:{Model}.",
                model, cache.Write, cache.Read, rates.LongPromptApplied ? " (LongPrompt tier)" : string.Empty, model);
            return new PriceLookupResult(PricingStatus.NoCacheRate, entry, 0m, rates.LongPromptApplied);
        }

        var write = Math.Max(0, cache.Write);
        var read = Math.Max(0, cache.Read);
        var uncached = Math.Max(0, inputTokens - write - read);
        var cost = (uncached / PerMillion * rates.InputPerMillion)
                 + (write / PerMillion * (rates.CacheWritePerMillion ?? 0m))
                 + (read / PerMillion * (rates.CacheReadPerMillion ?? 0m))
                 + (outputTokens / PerMillion * rates.OutputPerMillion);
        return new PriceLookupResult(PricingStatus.Priced, entry, cost, rates.LongPromptApplied);
    }
}
