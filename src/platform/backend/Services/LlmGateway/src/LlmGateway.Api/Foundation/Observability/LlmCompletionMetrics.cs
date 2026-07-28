using System.Diagnostics.Metrics;
using LlmGateway.Api.Foundation.Routing;
using Microsoft.Extensions.Options;
using Platform.Shared.Contracts.Dtos;

namespace LlmGateway.Api.Foundation.Observability;

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

    private const string DefaultPurpose = "default";

    private readonly Counter<long> _completions;
    private readonly IOptionsMonitor<LlmRoutingOptions> _routing;

    public LlmCompletionMetrics(IMeterFactory meterFactory, IOptionsMonitor<LlmRoutingOptions> routing)
    {
        _routing = routing;
        var meter = meterFactory.Create(MeterName);
        _completions = meter.CreateCounter<long>(
            CompletionCounterName, unit: "{completion}",
            description: "LLM 補完の呼び出し結果（送信可否 × 終了理由）。拒否率は "
                       + "stop_reason=refusal ÷ result=sent で求める。");
    }

    // 補完 1 回を計上する。**送信していない経路も計上する**（分母が欠けると拒否率が過大に見える）。
    //   result     — 送信可否の軸（ResultSent / ResultEgressDenied / ResultProviderMissing / ResultUpstreamError）
    //   stopReason — モデル側の終了理由。未送信・未報告は null（属性は none になる）
    public void RecordCompletion(
        string result, string? stopReason, RoutingDecision decision, string purpose, SensitivityClass sensitivity)
        => _completions.Add(1,
            new KeyValuePair<string, object?>(ResultTag, result),
            new KeyValuePair<string, object?>(StopReasonTag, NormalizeStopReason(stopReason)),
            new KeyValuePair<string, object?>(PurposeTag, NormalizePurpose(purpose)),
            new KeyValuePair<string, object?>(ModelTag, Or(decision.Model, ValueNone)),
            new KeyValuePair<string, object?>(ProviderTag, Or(decision.Provider, ValueNone)),
            new KeyValuePair<string, object?>(ConfidentialityTag, sensitivity.ToString().ToLowerInvariant()));

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
    // "default" は現行 appsettings の PurposeModels にも存在するが、設定に依存せず**常に**既知として扱う。
    // これはエンドポイント自身が purpose 未指定時に補う値（CompletionEndpoints の `?? "default"`）であり、
    // 設定から消えた場合に「呼び出し側は何も指定していないのに other が増える」誤検知になるためである。
    //
    // PurposeModels は LlmRoutingOptions 側で StringComparer.OrdinalIgnoreCase の辞書として初期化されており
    // （設定バインダはその辞書インスタンスへマージする）、キー照合は大小文字非依存になる。
    private string NormalizePurpose(string purpose)
    {
        if (string.Equals(purpose, DefaultPurpose, StringComparison.OrdinalIgnoreCase))
            return DefaultPurpose;
        return _routing.CurrentValue.PurposeModels.ContainsKey(purpose) ? purpose.ToLowerInvariant() : ValueOther;
    }

    private static string Or(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;
}
