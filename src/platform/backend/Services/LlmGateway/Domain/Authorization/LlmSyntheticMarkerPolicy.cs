using System.Diagnostics;
using System.Diagnostics.Metrics;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Routing;
using Microsoft.Extensions.Options;

namespace LlmGateway.Domain.Authorization;

// NFR-02, NFR-18, NFR-21, ADR-0044, ADR-0076 決定 4, IADR-0378, IADR-0701: 合成監視の標識（`X-Synthetic-Traffic`）を
// **引き継いでよい呼び手**の許可集合（`Llm:SyntheticMarker`）。
//
// 標識の付いた補完は費用の計上（`llm.cost.total`・月次予算の警報の元）から外れる。標識を外周から運ぶ正規の呼び手は限られるので、
// ゲートウェイは**検証済みのトークンの主体**（呼び手）が許可集合に在るときだけ標識を引き継ぐ。
// 🔴 既定の段は Report（従前どおり引き継ぎ、許可集合の外の呼び手が付けた標識を数えて報告する）。
//    Enforce では許可集合の外の標識を無視し、実利用として費用に計上する。送信そのものは段に依らず止めない。
// 🔴 許可集合が空なら、Enforce ではどの標識も引き継がない（費用に計上する側へ倒す）。
public sealed class LlmSyntheticMarkerOptions
{
    public const string SectionName = "Llm:SyntheticMarker";

    public LlmSyntheticMarkerMode Mode { get; set; } = LlmSyntheticMarkerMode.Report;

    // 標識を引き継いでよい呼び手（トークンのクライアント識別子）。大文字小文字は区別しない。
    public string[] TrustedCallers { get; set; } = [];
}

public enum LlmSyntheticMarkerMode
{
    // 段 1: 許可集合の外の標識も引き継ぐ（数えて報告するだけ）。
    Report,

    // 段 2: 許可集合の外の標識を無視する（費用に計上する）。
    Enforce,
}

public sealed class LlmSyntheticMarkerOptionsValidator : IValidateOptions<LlmSyntheticMarkerOptions>
{
    public ValidateOptionsResult Validate(string? name, LlmSyntheticMarkerOptions options)
    {
        var errors = new List<string>();
        if (!Enum.IsDefined(options.Mode))
            errors.Add($"{LlmSyntheticMarkerOptions.SectionName}:Mode が不正です（{options.Mode}）。Report か Enforce を指定してください。");
        if (options.TrustedCallers.Any(string.IsNullOrWhiteSpace))
            errors.Add($"{LlmSyntheticMarkerOptions.SectionName}:TrustedCallers に空の呼び手の名前があります。");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

// 🔴 **判定の唯一の点。** REST（/complete）と gRPC（Complete・CompleteStream）の両方の面が、判定器へ渡す前にここを通す。
public sealed class LlmSyntheticMarkerPolicy
{
    public const string UntrustedCounterName = "llm.synthetic_marker.untrusted.total";
    public const string ModeTag = "llm.marker.mode";

    private readonly IOptionsMonitor<LlmSyntheticMarkerOptions> _options;
    private readonly IOptionsMonitor<LlmCallerPolicyOptions> _callers;
    private readonly LogOccurrenceThrottle _throttle;
    private readonly ILogger<LlmSyntheticMarkerPolicy> _logger;
    private readonly Counter<long> _untrusted;

    public LlmSyntheticMarkerPolicy(
        IOptionsMonitor<LlmSyntheticMarkerOptions> options, IOptionsMonitor<LlmCallerPolicyOptions> callers,
        IMeterFactory meterFactory, LogOccurrenceThrottle throttle, ILogger<LlmSyntheticMarkerPolicy> logger)
    {
        _options = options;
        _callers = callers;
        _throttle = throttle;
        _logger = logger;
        _untrusted = meterFactory.Create(LlmCompletionMetrics.MeterName).CreateCounter<long>(
            UntrustedCounterName, unit: "{completion}",
            description: "合成監視の標識を、引き継いでよい呼び手の許可集合の外の呼び手が付けた補完の件数。"
                       + "llm.marker.mode=report は標識を引き継いだ（段 1）、enforce は無視して費用に計上した（段 2）。");
    }

    // 標識（markerPresent）と呼び手から、この補完を合成として扱うか（費用の計上から外すか）を決める。
    public bool IsSynthetic(string? caller, bool markerPresent)
    {
        if (!markerPresent) return false;

        var o = _options.CurrentValue;
        var who = string.IsNullOrWhiteSpace(caller) ? LlmCallerPolicy.UnknownCaller : caller.Trim();
        if (o.TrustedCallers.Any(t => string.Equals(t.Trim(), who, StringComparison.OrdinalIgnoreCase)))
            return true;

        var enforce = o.Mode == LlmSyntheticMarkerMode.Enforce;
        var mode = enforce ? "enforce" : "report";
        // 呼び手の属性は値域を閉じる（許可表にある名前か other。LlmCallerPolicy と同じ）。
        var label = LlmCallerPolicy.CallerLabel(_callers.CurrentValue, who);
        _untrusted.Add(1, new TagList { { ModeTag, mode }, { LlmCallerPolicy.CallerTag, label } });
        if (_throttle.ShouldLog($"synthetic-marker|{label}", out var suppressed))
            _logger.LogWarning(
                "Synthetic-traffic marker from a caller outside Llm:SyntheticMarker:TrustedCallers: caller={Caller} mode={MarkerMode} "
                + "suppressedSinceLast={Suppressed} (excludedFromCost={Excluded}; logged at most once per {Interval})",
                LlmRouter.Sanitize(who), mode, suppressed, !enforce, LogOccurrenceThrottle.SummaryInterval);
        return !enforce;
    }
}
