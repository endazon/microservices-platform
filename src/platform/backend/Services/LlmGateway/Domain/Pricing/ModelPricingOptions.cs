namespace LlmGateway.Domain.Pricing;

// FR-10, NFR, ADR-0006, ADR-0044 決定 3 (#443): モデル単価表。**有効期間つきの設定として持ち、
// コード内の定数にしない。** 導入価格には終了日があり（例: claude-sonnet-5 の $2/$10 は 2026-08-31 まで、
// 9 月以降 $3/$15）、**期限切れを反映し忘れると試算がエラーを出さずに過小になる**ためである。
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

    // 指定時刻がこの区間に含まれるか（[From, To)）。
    public bool Covers(DateTimeOffset at)
        => (EffectiveFrom is null || at >= EffectiveFrom.Value)
        && (EffectiveTo is null || at < EffectiveTo.Value);
}
