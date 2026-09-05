using System.Diagnostics;
using System.Diagnostics.Metrics;
using LlmGateway.Domain.Pricing;
using LlmGateway.Domain.Routing;
using Microsoft.Extensions.Options;

namespace LlmGateway.Common.Observability;

// FR-10, NFR, ADR-0006, ADR-0044 決定 1・3 (#443): LLM 利用実績（トークン消費量と金額換算）を
// **用途別・モデル別**に計上する。
//
// **総額のみの計測は採らない**（ADR-0044 §検討した選択肢）。どの用途がどのモデルで費用を出しているかを
// 分解できないと、モデル振り分け（ADR-0022 / ADR-0025 / ADR-0038）の効果を測れず、費用が増えたときに
// 「利用が増えた」のか「振り分けが崩れた」のかを判別できない。**属性は発行時点で決まり、
// 総額だけを計上してからでは過去分を分解できない。**
//
// **既存の計器との役割分担**:
//   - `llm.completion.total`（IADR-0110）= 呼び出し**回数**。拒否率・送信可否の分母。
//   - `llm.completion.output_tokens`（IADR-0212）= 出力トークンの**分布**。max_tokens の妥当性を
//     上限付近のバケットの厚みで読む（#380 の実測がこれを使う）。
//   - **本クラス** = トークンの**累計**と**金額**。費用の分子であり、分布では合算できない。
//     ★ #380 と重ならない —— 向こうは分布を読んで max_tokens を再調整する作業、
//       こちらは累計から費用を出す計器である。**Histogram は一切変更していない。**
//
// **属性の値域は IADR-0110 の規律をそのまま継承する**（LlmMetricValues）。
// **利用者識別子・プロンプト・本文は属性にしない**（ADR-0044 決定 1。カーディナリティが非有界であり、
// 個人の利用行動の記録に踏み込む）。
public sealed class LlmUsageMetrics
{
    // Meter は補完カウンタと同じものを使う（サービス名と一致。Program.cs の AddMeter は 1 つで足りる）。
    public const string MeterName = LlmCompletionMetrics.MeterName;

    public const string TokensCounterName = "llm.tokens.total";
    public const string CostCounterName = "llm.cost.total";
    public const string UnpricedCounterName = "llm.pricing.unpriced.total";

    // NFR-02, ADR-0076 決定 4, [[IADR-0378]] (#1203): **合成監視のため費用へ計上しなかった**呼び出しの件数。
    // 🔴 **黙って落とさない。** 除外を数えないと「合成だけが通っていて実利用は 0」でも
    // 費用ダッシュボードが平常に見え、**指標を守るための除外が、指標を読めなくする**。
    public const string SyntheticExcludedCounterName = "llm.usage.synthetic_excluded.total";

    public const string TokenTypeTag = "llm.token_type";
    public const string CurrencyTag = "llm.currency";
    public const string PricingStatusTag = "llm.pricing_status";

    public const string TokenTypeInput = "input";
    public const string TokenTypeOutput = "output";

    // 単価が解決できなかった理由（PricingStatus の属性表現）。値域は 2 値で閉じる。
    public const string PricingOutOfPeriod = "out_of_period";
    public const string PricingNoEntry = "no_entry";

    private readonly Counter<long> _tokens;
    private readonly Counter<double> _cost;
    private readonly Counter<long> _unpriced;
    private readonly Counter<long> _syntheticExcluded;
    private readonly IOptionsMonitor<LlmRoutingOptions> _routing;
    private readonly ModelPriceTable _prices;
    private readonly TimeProvider _clock;

    public LlmUsageMetrics(
        IMeterFactory meterFactory,
        IOptionsMonitor<LlmRoutingOptions> routing,
        ModelPriceTable prices,
        TimeProvider clock)
    {
        _routing = routing;
        _prices = prices;
        _clock = clock;
        var meter = meterFactory.Create(MeterName);
        _tokens = meter.CreateCounter<long>(
            TokensCounterName, unit: "{token}",
            description: "送信が成立した LLM 補完のトークン消費量の累計（用途別・モデル別。入出力は "
                       + "llm.token_type で分ける）。費用の分子である。");
        _cost = meter.CreateCounter<double>(
            CostCounterName, unit: "{currency}",
            description: "LLM 補完の金額換算の累計（用途別・モデル別）。換算は有効期間つき単価表を読む "
                       + "ゲートウェイ側で行う（ADR-0044 決定 3。Grafana のクエリに単価を書かない）。");
        _unpriced = meter.CreateCounter<long>(
            UnpricedCounterName, unit: "{completion}",
            description: "単価を解決できず金額を計上できなかった呼び出しの件数。**0 でない値は単価表の "
                       + "期限切れ・登録漏れの検出である**（無音で 0 円にしないための警報）。");
        _syntheticExcluded = meter.CreateCounter<long>(
            SyntheticExcludedCounterName, unit: "{completion}",
            description: "合成監視（synthetic）のため llm.tokens.total / llm.cost.total へ計上しなかった "
                       + "補完の件数（ADR-0076 決定 4）。**この系列が伸び、実利用の費用が伸びないときは "
                       + "「合成だけが通っていて実利用は 0」である**。除外は指標を守るためであり、"
                       + "費用そのものは減らない（同 §残るもの）。");
    }

    // 送信が成立した補完 1 回分の利用実績を計上する。
    //
    // **未送信の経路では呼ばない。** 越境拒否・プロバイダ未登録・上流エラーにトークンは存在せず、
    // 0 を積むと「安く済んだ」と読める（IADR-0212 決定 3 と同じ判断）。
    //
    // at: 単価の有効期間を判定する時刻。既定は現在時刻。**呼び出し時刻で判定する**——
    //     集計期間が単価改定をまたいでも、各呼び出しはその時点の単価で換算されるため、
    //     期間をまたぐ集計が正しい金額になる。
    public void RecordUsage(
        RoutingDecision decision, string purpose, SensitivityClass sensitivity,
        long inputTokens, long outputTokens, DateTimeOffset? at = null)
    {
        var occurredAt = at ?? _clock.GetUtcNow();
        var routing = _routing.CurrentValue;
        var model = LlmMetricValues.Or(decision.Model, LlmMetricValues.None);

        var baseTags = new TagList
        {
            { LlmCompletionMetrics.PurposeTag, LlmMetricValues.NormalizePurpose(routing, purpose) },
            { LlmCompletionMetrics.ModelTag, model },
            { LlmCompletionMetrics.ProviderTag, LlmMetricValues.Or(decision.Provider, LlmMetricValues.None) },
            { LlmCompletionMetrics.ConfidentialityTag, sensitivity.ToString().ToLowerInvariant() },
        };

        // トークンは入出力で単価が違うため、属性で分けて 1 本の計器に載せる
        // （計器を 2 本に分けると PromQL 側で足し合わせる式が要り、用途別の内訳が読みにくくなる）。
        RecordTokens(baseTags, TokenTypeInput, inputTokens);
        RecordTokens(baseTags, TokenTypeOutput, outputTokens);

        var estimate = _prices.Estimate(decision.Model, inputTokens, outputTokens, occurredAt);
        if (!estimate.IsPriced)
        {
            // ADR-0044 決定 3: **無音で 0 円として扱わない。** 金額は積まず、解決できなかったことを計上する。
            var unpricedTags = new TagList
            {
                { PricingStatusTag, estimate.Status == PricingStatus.OutOfEffectivePeriod ? PricingOutOfPeriod : PricingNoEntry },
                { LlmCompletionMetrics.ModelTag, model },
            };
            _unpriced.Add(1, unpricedTags);
            return;
        }

        var costTags = baseTags;
        costTags.Add(CurrencyTag, _prices.Currency);
        _cost.Add((double)estimate.Cost, costTags);
    }

    // NFR-02, ADR-0044, ADR-0076 決定 4, [[IADR-0378]] (#1203): 合成監視の呼び出しを費用から外す。
    //
    // 🔴 **`RecordUsage` の代わりに呼ぶ**（両方は呼ばない）。属性は用途とモデルだけで、
    // `RecordUsage` と同じ正規化を通す —— **軸が違うと「除外したぶん」と「計上したぶん」を並べて読めない**
    // （ADR-0044 §理由 が用途別・モデル別の分解を要求したのと同じ理由）。
    //
    // 🔴 **トークン数は属性にしない。** 非有界であり、載せるとカーディナリティが爆発する
    // （[[IADR-0110]] の規律）。除外した量が要るなら `llm.completion.output_tokens` の分布を読む。
    public void RecordSyntheticExclusion(
        RoutingDecision decision, string purpose, SensitivityClass sensitivity)
    {
        var routing = _routing.CurrentValue;
        _syntheticExcluded.Add(1, new TagList
        {
            { LlmCompletionMetrics.PurposeTag, LlmMetricValues.NormalizePurpose(routing, purpose) },
            { LlmCompletionMetrics.ModelTag, LlmMetricValues.Or(decision.Model, LlmMetricValues.None) },
            { LlmCompletionMetrics.ProviderTag, LlmMetricValues.Or(decision.Provider, LlmMetricValues.None) },
            { LlmCompletionMetrics.ConfidentialityTag, sensitivity.ToString().ToLowerInvariant() },
        });
    }

    private void RecordTokens(TagList baseTags, string tokenType, long tokens)
    {
        if (tokens <= 0)
            return;
        var tags = baseTags;
        tags.Add(TokenTypeTag, tokenType);
        _tokens.Add(tokens, tags);
    }
}
