using LlmGateway.Domain.Authorization;

namespace LlmGateway.Domain.Limits;

// NFR-18, NFR-21, FR-11, ADR-0044, IADR-0678 (#2404): 送る前の呼び出しの上限（`Llm:CallLimits`）。
//
// 🔴 **段（Mode）の既定は Report である。** 判定して報告するだけで送信は止めない（段 1）。
// 送信を止めるのは Enforce へ設定で切り替えたときだけ（段 2）。稼働の呼び出しを報告なしで止めないためである。
//
// 🔴 **費用の上限に金額の既定は置かない**（IADR-0466 と同じ判断。金額は所有者が設定する）。
// ［2026-11-07 / IADR-0692］ただし **Enforce では `Default.DailyCostLimit` が無いと起動しない**（LlmCallLimitOptionsValidator）。
// 要求数・入力の上限は技術的な既定値を持つ（既知の呼び手の最大より上。IADR-0678 決定 2）。
public sealed class LlmCallLimitOptions
{
    public const string SectionName = "Llm:CallLimits";

    public LlmCallLimitMode Mode { get; set; } = LlmCallLimitMode.Report;

    // NFR-18, NFR-21, IADR-0701: 埋め込み（gRPC `LlmEmbedding/Embed`）の上限の段。補完の段（Mode）とは別に切り替える。
    // 🔴 既定は Report（判定して報告だけ）。Off は判定も計上もしない（従前の挙動）。止めるのは Enforce のときだけ。
    // 判定するとき（Report・Enforce）は、補完と同じ呼び手の要求数・当日の費用の枠に数える（用途の枠は固定の `embedding`）。
    public LlmGuardStage EmbeddingMode { get; set; } = LlmGuardStage.Report;

    // `max_tokens` の上限（1 以上この値以下を許す）。
    public int MaxOutputTokens { get; set; } = 32768;

    // プロンプトの長さの上限（`CacheablePrefix` ＋ `Prompt` の文字数）。
    public int MaxPromptChars { get; set; } = 800_000;

    // 呼び手ごとの既定。`Callers` に項目が無い呼び手と、項目が書かなかった値に使う。
    public LlmCallerLimits Default { get; set; } = new()
    {
        RequestsPerMinute = 300,
        PurposeRequestsPerMinute = 120,
    };

    // AI レビュー #2408: 単価表に無いモデルの見込み額・実額に使う計上単価（単価表の通貨・百万トークンあたり）。
    // 🔴 **0 にしない**（0 だと単価の無いモデルで費用の上限が黙って効かない）。既定は入力 10・出力 50
    // （AST の「表に無いモデルの計上単価」の既定と同じ値。多めに見積もる側へ倒す。IADR-0678 決定 2）。
    public LlmUnknownModelPrice UnknownModelPrice { get; set; } = new();

    // NFR-18, NFR-21, IADR-0701: 埋め込みのモデル → 入力の百万トークンあたりの計上単価（単価表の通貨）。
    // 補完の単価表（Llm:Pricing:Models）は補完のモデルだけを持つ（計画 ADR-0138 決定 7）ので、埋め込みの単価はここに置く。
    // 🔴 ここに無いモデルは 0 にせず UnknownModelPrice の入力単価で数える（多めの側）。外へ出ない経路（ティア A）は費用を数えない。
    public Dictionary<string, decimal> EmbeddingPrices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // 呼び手（トークンのクライアント識別子）→ 上書き。**書かなかった項目は Default の値を使う**。
    public Dictionary<string, LlmCallerLimits> Callers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class LlmUnknownModelPrice
{
    public decimal InputPerMillionTokens { get; set; } = 10m;
    public decimal OutputPerMillionTokens { get; set; } = 50m;
}

public enum LlmCallLimitMode
{
    // 段 1: 判定して報告だけ（送信は止めない）。
    Report,

    // 段 2: 超えた呼び出しを送らず Sent=false で返す。
    Enforce,
}

// 呼び手 1 つ分の上限。null は「この項目を書いていない」（Callers では Default へ倒す）。
public sealed class LlmCallerLimits
{
    // 呼び手の直近 60 秒の件数の上限（全用途の合計）。
    public int? RequestsPerMinute { get; set; }

    // 呼び手 × 用途の直近 60 秒の件数の上限（用途ごとに同じ値）。
    public int? PurposeRequestsPerMinute { get; set; }

    // 呼び手の UTC の当日の費用の上限（単価表の通貨。全用途の合計）。
    public decimal? DailyCostLimit { get; set; }

    // 用途 → 呼び手 × 用途の UTC の当日の費用の上限。キーは既知の用途・default・other・embedding（埋め込み。IADR-0701）。
    public Dictionary<string, decimal> PurposeDailyCostLimits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

// 呼び手 1 つに効く値（Callers の上書きと Default を合わせた結果）。
public sealed record EffectiveCallerLimits(
    int? RequestsPerMinute,
    int? PurposeRequestsPerMinute,
    decimal? DailyCostLimit,
    IReadOnlyDictionary<string, decimal> PurposeDailyCostLimits)
{
    public bool HasCostLimit => DailyCostLimit is not null || PurposeDailyCostLimits.Count > 0;

    public static EffectiveCallerLimits For(LlmCallLimitOptions options, string caller)
    {
        var d = options.Default;
        if (!options.Callers.TryGetValue(caller, out var c))
            return new(d.RequestsPerMinute, d.PurposeRequestsPerMinute, d.DailyCostLimit, d.PurposeDailyCostLimits);

        // 用途ごとの費用の上限は、Default の表に呼び手の表を重ねる（呼び手の値が勝つ）。
        var purposeCosts = new Dictionary<string, decimal>(d.PurposeDailyCostLimits, StringComparer.OrdinalIgnoreCase);
        foreach (var (purpose, limit) in c.PurposeDailyCostLimits)
            purposeCosts[purpose] = limit;

        return new(
            c.RequestsPerMinute ?? d.RequestsPerMinute,
            c.PurposeRequestsPerMinute ?? d.PurposeRequestsPerMinute,
            c.DailyCostLimit ?? d.DailyCostLimit,
            purposeCosts);
    }
}
