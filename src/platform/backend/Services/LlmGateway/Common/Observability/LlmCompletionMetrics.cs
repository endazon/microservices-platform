using System.Diagnostics;
using System.Diagnostics.Metrics;
using LlmGateway.Domain.Routing;
using Microsoft.Extensions.Options;
using Platform.Shared.Contracts.Dtos;

namespace LlmGateway.Common.Observability;

// FR-11, NFR, ADR-0006, IADR-0110 (#395): 補完（completion）の終了理由を計上するカウンタ。
// IADR-0104 で終了理由はログに残るようになったが、拒否がどの頻度で起きているかを継続的に把握する
// 手段が無かった。拒否率は既定モデルの妥当性・特定用途の恒常的拒否・障害と拒否の切り分けに直結する。
//
// 設計の要点は「属性値の値域を閉じること」である。時系列の系列数は属性値の直積で増えるため、
// 非有界な値を 1 つ混ぜるとカーディナリティが爆発する。本経路では purpose（呼び出し側の自由文字列）と
// stop_reason（未知値を原文透過する。IADR-0104 / IADR-0109）が非有界になり得るため、いずれも
// 既知集合以外は other へ集約する。原文が必要な調査はログ側で行う（メトリクスは傾向、ログは個別）。
// プロンプト・本文・利用者識別子は属性にしない。
public sealed class LlmCompletionMetrics
{
    // Meter 名はサービス名と一致させる（Program.cs の AddMeter と OTLP の収集対象）。
    public const string MeterName = "microservices-platform.llm-gateway";
    public const string CompletionCounterName = "llm.completion.total";
    public const string OutputTokensHistogramName = "llm.completion.output_tokens";

    public const string ResultTag = "llm.result";
    public const string StopReasonTag = "llm.stop_reason";
    public const string PurposeTag = "llm.purpose";
    public const string ModelTag = "llm.model";
    public const string ProviderTag = "llm.provider";
    public const string ConfidentialityTag = "llm.confidentiality";

    // llm.result: 送信可否の軸（FR-11 の Sent に対応）。stop_reason とは独立した軸である（IADR-0104）。
    public const string ResultSent = "sent";                     // 越境が成立した（拒否率の分母）
    public const string ResultEgressDenied = "egress_denied";    // 機密区分により送信しなかった
    public const string ResultProviderMissing = "provider_missing"; // 呼び出し先プロバイダ未登録
    public const string ResultUpstreamError = "upstream_error";  // 呼び出し先が不調（例外）
    // ADR-0038 決定 6 (#863), IADR-0225: 上流が HTTP 400 系を返し、**次の候補モデルへ切り替えた**呼び出し。
    // フォールバックが起きた 1 リクエストは 2 回計上される（見送った第 1 候補が fallback、
    // 成功した第 2 候補が sent）。llm.model が候補ごとに違うため、用途別・モデル別の利用実績として読める。
    // ★ upstream_error に混ぜない —— 混ぜると「回復した呼び出し」が呼び出し先障害の率へ入り、
    //   upstream_error 率 > 10%（critical）のアラート方針が誤発火する。
    public const string ResultFallback = "fallback";

    // 未知値の集約先と「該当なし」。
    public const string ValueOther = "other";
    public const string ValueNone = "none";

    // メトリクス属性として許す終了理由（正準語彙。IADR-0104 / IADR-0109）。
    private static readonly HashSet<string> KnownStopReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        CompletionStopReasons.EndTurn,
        CompletionStopReasons.MaxTokens,
        CompletionStopReasons.Refusal,
        CompletionStopReasons.StopSequence,
        CompletionStopReasons.ToolUse,
    };

    // IADR-0212 (#786): 既定の max_tokens 4096（IADR-0101）の妥当性を読むための境界。
    // 上限付近を細かく刻む（2048 / 3072 / 4096）—— 「上限に張り付いているのか、余裕があるのか」が
    // 4096 の再調整の判断材料そのものだからである。4096 超は既定を上げた場合の観測用に 1 段だけ置く。
    private static readonly int[] OutputTokenBuckets =
        [0, 16, 64, 128, 256, 512, 1024, 2048, 3072, 4096, 8192];

    private readonly Counter<long> _completions;
    private readonly Histogram<int> _outputTokens;
    private readonly IOptionsMonitor<LlmRoutingOptions> _routing;

    public LlmCompletionMetrics(IMeterFactory meterFactory, IOptionsMonitor<LlmRoutingOptions> routing)
    {
        _routing = routing;
        var meter = meterFactory.Create(MeterName);
        _completions = meter.CreateCounter<long>(
            CompletionCounterName, unit: "{completion}",
            description: "LLM 補完の呼び出し結果（送信可否 × 終了理由）。拒否率は "
                       + "stop_reason=refusal ÷ result=sent で求める。");
        // 送信が成立した呼び出しだけを分布として持つ（未送信にはトークン数が存在しない。IADR-0212 決定 3）。
        _outputTokens = meter.CreateHistogram<int>(
            OutputTokensHistogramName, unit: "{token}",
            description: "送信が成立した LLM 補完の出力トークン数の分布。max_tokens の妥当性は "
                       + "上限付近のバケットの厚みで読む（IADR-0101 / #380）。",
            tags: null,
            advice: new InstrumentAdvice<int> { HistogramBucketBoundaries = OutputTokenBuckets });
    }

    // 補完 1 回を計上する。**送信していない経路も計上する**（分母が欠けると拒否率が過大に見える）。
    //   result       — 送信可否の軸（ResultSent / ResultEgressDenied / ResultProviderMissing / ResultUpstreamError）
    //   stopReason   — モデル側の終了理由。未送信・未報告は null（属性は none になる）
    //   outputTokens — 出力トークン数。**送信が成立した経路だけが値を持つ**（IADR-0212 決定 3）。
    //                  null の経路では Histogram を記録しない —— 0 を積むと分布の最下段が
    //                  「短い応答」と「応答が無かった」の混合になり、上限到達の判断が濁る。
    public void RecordCompletion(
        string result, string? stopReason, RoutingDecision decision, string purpose,
        SensitivityClass sensitivity, int? outputTokens = null)
    {
        var tags = new TagList
        {
            { ResultTag, result },
            { StopReasonTag, NormalizeStopReason(stopReason) },
            { PurposeTag, NormalizePurpose(purpose) },
            { ModelTag, Or(decision.Model, ValueNone) },
            { ProviderTag, Or(decision.Provider, ValueNone) },
            { ConfidentialityTag, sensitivity.ToString().ToLowerInvariant() },
        };
        _completions.Add(1, tags);

        if (outputTokens is not { } tokens)
            return;
        // llm.result は Histogram では常に sent になり系列を分けないため落とす（IADR-0212 決定 2）。
        var histogramTags = new TagList();
        foreach (var tag in tags)
        {
            if (tag.Key != ResultTag)
                histogramTags.Add(tag);
        }
        _outputTokens.Record(tokens, histogramTags);
    }

    // 正準語彙のみ属性に載せ、未知値は other へ集約する。プロバイダ側の語彙追加が系列増加に直結しないようにする。
    private static string NormalizeStopReason(string? stopReason)
    {
        if (string.IsNullOrWhiteSpace(stopReason))
            return ValueNone;
        return KnownStopReasons.Contains(stopReason) ? stopReason.ToLowerInvariant() : ValueOther;
    }

    // purpose は呼び出し側が自由に指定できるため、設定（PurposeModels）で値域を閉じる。
    // other の増加は「定義していない purpose が来ている」＝ルーティングが既定へ落ちている状態の遅い警報にもなる。
    //
    // ［#443］正規化の実体は LlmMetricValues へ出した。**費用系の計器（LlmUsageMetrics）と同じ軸で
    // 読めなければ用途別モデル振り分けの効果を測れない**ため（ADR-0044 決定 1）、値域の定義を 1 箇所に持つ。
    //
    // PurposeModels は LlmRoutingOptions 側で StringComparer.OrdinalIgnoreCase の辞書として初期化されており
    // （設定バインダはその辞書インスタンスへマージする）、キー照合は大小文字非依存になる。
    private string NormalizePurpose(string purpose)
        => LlmMetricValues.NormalizePurpose(_routing.CurrentValue, purpose);

    private static string Or(string? value, string fallback)
        => LlmMetricValues.Or(value, fallback);
}
