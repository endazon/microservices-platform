namespace LlmGateway.Domain.Pricing;

// FR-10, NFR, ADR-0006, ADR-0044 決定 3 (#443): モデル単価表。**有効期間つきの設定として持ち、
// コード内の定数にしない。** 単価には改定があり、**改定を反映し忘れると試算がエラーを出さずに過小になる**ためである。
// 逆に**予定されていた改定の中止を反映し忘れると、試算は静かに過大になる**（#1741: claude-sonnet-5 の
// $3/$15 への引き上げは中止され $2/$10 が標準価格のまま。計画 ADR-0044 の 2026-09-09 訂正）。
public sealed class ModelPricingOptions
{
    public const string SectionName = "Llm:Pricing";

    // 単価の通貨。一次情報（プロバイダの公開単価）が USD であるため既定は USD とし、
    // 為替換算は行わない（レート取得という新しい外部依存を費用計算へ持ち込まない）。
    public string Currency { get; set; } = "USD";

    // モデル名 → 有効期間つき単価の一覧。**同一モデルの区間は重なってはならない**
    // （重なりは ModelPricingOptionsValidator が起動時に落とす）。
    public Dictionary<string, List<ModelPriceEntry>> Models { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

// ADR-0044 決定 3: 1 区間分の単価。**区間は半開区間 `[EffectiveFrom, EffectiveTo)` である。**
//
// 🔴 **終了側を「含む」にしてはならない。** 単価改定は「終了日 = 次の開始日」で書くのが自然であり、
// 両端を含むと**切替時刻ちょうどに 2 つの区間が該当**して、どちらで換算したかが後から分からなくなる。
// 逆に終了日を「前日の 23:59:59」で書かせると、その 1 秒の隙間に落ちた呼び出しが**単価なし**になる。
// 半開区間はこのどちらも起こさない。
public sealed class ModelPriceEntry
{
    // 省略時は過去方向に無限（この単価より前の期間は存在しない）。
    public DateTimeOffset? EffectiveFrom { get; set; }

    // 省略時は未来方向に無限（現行価格）。**含まない**。
    public DateTimeOffset? EffectiveTo { get; set; }

    // 百万トークンあたりの入力単価。
    public decimal InputPerMillionTokens { get; set; }

    // 百万トークンあたりの出力単価。
    public decimal OutputPerMillionTokens { get; set; }

    // FR-11, ADR-0044 決定 3, IADR-0529 (#1875・planning#783): **プロンプト長で単価が変わるモデルの上段。**
    // 省略時（null）は 1 段（従前どおり）。`claude-haiku-5-5` は、1 要求の入力トークンの合計が
    // 100,000 を**超える**とその要求の入力・出力の両方が上段の単価になる（提供元の公表値。2026-10-10 確認）。
    // 🔴 上段へ寄せた 1 段で持たない —— 大半の要求（≦100K）を 5 倍に過大計上し、費用の増加の検知が鈍る。
    public LongPromptPrice? LongPrompt { get; set; }

    // 指定時刻がこの区間に含まれるか（[From, To)）。
    public bool Covers(DateTimeOffset at)
        => (EffectiveFrom is null || at >= EffectiveFrom.Value)
        && (EffectiveTo is null || at < EffectiveTo.Value);

    // この要求の入力トークン数で適用する単価（入力, 出力, 上段か）を返す。
    // 境界ちょうど（= ThresholdInputTokens）は**下段**である（提供元の表記「100,000 トークン以下」に合わせる）。
    public (decimal InputPerMillion, decimal OutputPerMillion, bool LongPromptApplied) RatesFor(long inputTokens)
        => LongPrompt is { } lp && inputTokens > lp.ThresholdInputTokens
            ? (lp.InputPerMillionTokens, lp.OutputPerMillionTokens, true)
            : (InputPerMillionTokens, OutputPerMillionTokens, false);
}

// FR-11, ADR-0044 決定 3, IADR-0529: プロンプト長による上段の単価。区間（有効期間）は親の ModelPriceEntry に従う。
public sealed class LongPromptPrice
{
    // この値を**超える**入力トークン数の要求に上段を適用する（以下は下段）。1 以上（検証器が落とす）。
    public long ThresholdInputTokens { get; set; }

    // 上段の百万トークンあたりの入力単価。
    public decimal InputPerMillionTokens { get; set; }

    // 上段の百万トークンあたりの出力単価。
    public decimal OutputPerMillionTokens { get; set; }
}

