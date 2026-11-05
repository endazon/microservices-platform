using System.Diagnostics;
using System.Diagnostics.Metrics;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Routing;
using Microsoft.Extensions.Options;

namespace LlmGateway.Domain.Authorization;

// NFR-18, NFR-09, FR-11, ADR-0084 決定 1, IADR-0684 (#2417): 補完の呼び手ごとの許可表（`Llm:CallerPolicy`）。
//
// 門（ServiceCaller）は realm ロール `platform-service` を見るだけで、呼び手を区別しない。
// ここでは**呼び手（検証済みトークンのクライアント識別子）ごとに、使ってよい用途と、モデルの明示の指定を許すか**を決める。
// 🔴 既定の段は Report（判定して報告だけ）。拒むのは Enforce へ設定で切り替えたときだけ。
public sealed class LlmCallerPolicyOptions
{
    public const string SectionName = "Llm:CallerPolicy";

    public LlmCallerPolicyMode Mode { get; set; } = LlmCallerPolicyMode.Report;

    // 呼び手（クライアント識別子）→ 許可。**ここに無い呼び手は補完を呼べない**（段 2）。
    public Dictionary<string, LlmCallerGrant> Callers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum LlmCallerPolicyMode
{
    Report,
    Enforce,
}

public sealed class LlmCallerGrant
{
    // 使ってよい用途（要求の purpose。未指定の要求は `default`）。大文字小文字は区別しない。
    public List<string> Purposes { get; set; } = [];

    // 要求でモデルを明示してよいか（既定は不可。用途ごとの割当に従う）。
    public bool AllowModelOverride { get; set; }
}

public sealed class LlmCallerPolicyOptionsValidator : IValidateOptions<LlmCallerPolicyOptions>
{
    public ValidateOptionsResult Validate(string? name, LlmCallerPolicyOptions options)
    {
        var errors = new List<string>();
        if (!Enum.IsDefined(options.Mode))
            errors.Add($"{LlmCallerPolicyOptions.SectionName}:Mode が不正です（{options.Mode}）。Report か Enforce を指定してください。");
        foreach (var (caller, grant) in options.Callers)
        {
            if (string.IsNullOrWhiteSpace(caller))
                errors.Add($"{LlmCallerPolicyOptions.SectionName}:Callers に空の呼び手の名前があります。");
            if (grant.Purposes.Any(string.IsNullOrWhiteSpace))
                errors.Add($"{LlmCallerPolicyOptions.SectionName}:Callers:{caller}:Purposes に空の用途があります。");
        }
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

// 判定の結果。Allowed が偽なら拒む（Enforce のときだけ偽になる）。Violations は Report でも埋まる。
public sealed record LlmCallerDecision(bool Allowed, IReadOnlyList<string> Violations);

// 🔴 **判定の唯一の点。** REST（/complete）と gRPC（Complete・CompleteStream）の両方の面が、判定器（CompletionUseCase）を呼ぶ前にここを呼ぶ。
public sealed class LlmCallerPolicy
{
    public const string UnknownCaller = "unknown";

    // 計器の属性と抑止の鍵に使う、許可表に無い呼び手の集約先（値域を閉じる。用途の other と同じ扱い）。
    public const string OtherCaller = "other";

    // 違反の種類（llm.policy.kind）。3 値で閉じる。
    public const string KindUnknownCaller = "unknown_caller";
    public const string KindPurposeNotAllowed = "purpose_not_allowed";
    public const string KindModelOverride = "model_override_not_allowed";

    public const string ViolationCounterName = "llm.caller_policy.violation.total";
    public const string KindTag = "llm.policy.kind";
    public const string ModeTag = "llm.policy.mode";
    public const string CallerTag = "llm.caller";

    private readonly IOptionsMonitor<LlmCallerPolicyOptions> _options;
    private readonly IOptionsMonitor<LlmRoutingOptions> _routing;
    private readonly LogOccurrenceThrottle _throttle;
    private readonly ILogger<LlmCallerPolicy> _logger;
    private readonly Counter<long> _violations;

    public LlmCallerPolicy(
        IOptionsMonitor<LlmCallerPolicyOptions> options, IOptionsMonitor<LlmRoutingOptions> routing,
        IMeterFactory meterFactory, LogOccurrenceThrottle throttle, ILogger<LlmCallerPolicy> logger)
    {
        _options = options;
        _routing = routing;
        _throttle = throttle;
        _logger = logger;
        _violations = meterFactory.Create(LlmCompletionMetrics.MeterName).CreateCounter<long>(
            ViolationCounterName, unit: "{completion}",
            description: "補完の呼び手ごとの許可表（用途・モデルの明示の指定）に無い呼び出しの件数。"
                       + "llm.policy.mode=report は通した（段 1）、enforce は拒んだ（段 2）。");
    }

    public LlmCallerDecision Evaluate(string? caller, string? purpose, string? requestedModel)
    {
        var o = _options.CurrentValue;
        var who = string.IsNullOrWhiteSpace(caller) ? UnknownCaller : caller.Trim();
        var asked = string.IsNullOrWhiteSpace(purpose) ? LlmMetricValues.DefaultPurpose : purpose.Trim();

        var violations = new List<string>(1);
        if (!o.Callers.TryGetValue(who, out var grant))
        {
            violations.Add(KindUnknownCaller);
        }
        else
        {
            // 🔴 用途は**要求の原文**で照合する（正規化すると、未知の用途が other へ寄って許可表をすり抜け得る）。
            if (!grant.Purposes.Contains(asked, StringComparer.OrdinalIgnoreCase))
                violations.Add(KindPurposeNotAllowed);
            if (!string.IsNullOrWhiteSpace(requestedModel) && !grant.AllowModelOverride)
                violations.Add(KindModelOverride);
        }

        var enforce = o.Mode == LlmCallerPolicyMode.Enforce;
        if (violations.Count > 0)
        {
            var mode = enforce ? "enforce" : "report";
            // 🔴 計器の属性と抑止の鍵は**値域を閉じた値**だけで作る。用途は計器と同じ正規化、
            // 呼び手は許可表にある名前（小文字）か other。ログの本文には Sanitize した実名を出す。
            var normalized = LlmMetricValues.NormalizePurpose(_routing.CurrentValue, asked);
            var callerLabel = grant is null ? OtherCaller : who.ToLowerInvariant();
            foreach (var kind in violations)
            {
                _violations.Add(1, new TagList
                {
                    { KindTag, kind }, { ModeTag, mode }, { CallerTag, callerLabel }, { LlmCompletionMetrics.PurposeTag, normalized },
                });
                if (_throttle.ShouldLog($"caller-policy|{kind}|{callerLabel}|{normalized}", out var suppressed))
                    _logger.LogWarning(
                        "LLM caller policy violation: kind={PolicyKind} mode={PolicyMode} caller={Caller} purpose={Purpose} "
                        + "suppressedSinceLast={Suppressed} (allowed={Allowed}; logged at most once per {Interval})",
                        kind, mode, LlmRouter.Sanitize(who), LlmRouter.Sanitize(asked), suppressed, !enforce,
                        LogOccurrenceThrottle.SummaryInterval);
            }
        }

        return new LlmCallerDecision(!(enforce && violations.Count > 0), violations);
    }
}
