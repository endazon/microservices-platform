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

    // NFR-18, IADR-0702: 埋め込み（gRPC `LlmEmbedding/Embed`）の許可の段。補完の段（Mode）とは別に切り替える。
    // 🔴 既定は Report（判定して報告だけ）。Off は判定しない（従前の挙動）。拒むのは Enforce のときだけ。
    public LlmGuardStage EmbeddingMode { get; set; } = LlmGuardStage.Report;

    // 呼び手（クライアント識別子）→ 許可。**ここに無い呼び手は補完を呼べない**（段 2）。
    public Dictionary<string, LlmCallerGrant> Callers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum LlmCallerPolicyMode
{
    Report,
    Enforce,
}

// NFR-18, IADR-0702: 埋め込みの許可・上限の段（3 段）。Off は判定しない、Report は判定して報告だけ、Enforce は止める。
public enum LlmGuardStage
{
    Off,
    Report,
    Enforce,
}

public sealed class LlmCallerGrant
{
    // 使ってよい用途（要求の purpose。未指定の要求は `default`）。大文字小文字は区別しない。
    public List<string> Purposes { get; set; } = [];

    // 要求でモデルを明示してよいか（既定は不可。用途ごとの割当に従う）。
    public bool AllowModelOverride { get; set; }

    // NFR-18, IADR-0702: 埋め込み（gRPC `LlmEmbedding/Embed`）を呼んでよいか（既定は不可）。
    public bool AllowEmbedding { get; set; }
}

public sealed class LlmCallerPolicyOptionsValidator : IValidateOptions<LlmCallerPolicyOptions>
{
    public ValidateOptionsResult Validate(string? name, LlmCallerPolicyOptions options)
    {
        var errors = new List<string>();
        if (!Enum.IsDefined(options.Mode))
            errors.Add($"{LlmCallerPolicyOptions.SectionName}:Mode が不正です（{options.Mode}）。Report か Enforce を指定してください。");
        if (!Enum.IsDefined(options.EmbeddingMode))
            errors.Add($"{LlmCallerPolicyOptions.SectionName}:EmbeddingMode が不正です（{options.EmbeddingMode}）。Off・Report・Enforce のいずれかを指定してください。");
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
// ［2026-11-07 / IADR-0702］埋め込み（gRPC Embed）も、判定器（EmbedUseCase）を呼ぶ前に EvaluateEmbedding を呼ぶ。
public sealed class LlmCallerPolicy
{
    public const string UnknownCaller = "unknown";

    // 計器の属性と抑止の鍵に使う、許可表に無い呼び手の集約先（値域を閉じる。用途の other と同じ扱い）。
    public const string OtherCaller = "other";

    // 違反の種類（llm.policy.kind）。4 値で閉じる（embedding_not_allowed は IADR-0702）。
    public const string KindUnknownCaller = "unknown_caller";
    public const string KindPurposeNotAllowed = "purpose_not_allowed";
    public const string KindModelOverride = "model_override_not_allowed";
    public const string KindEmbeddingNotAllowed = "embedding_not_allowed";

    // 埋め込みの違反を計器へ載せるときの用途（固定の値。補完の用途の値域と混ざらない。IADR-0702）。
    public const string EmbeddingPurpose = "embedding";

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
            // 🔴 計器の属性と抑止の鍵は**値域を閉じた値**だけで作る。用途は計器と同じ正規化、
            // 呼び手は許可表にある名前（小文字）か other。ログの本文には Sanitize した実名を出す。
            var normalized = LlmMetricValues.NormalizePurpose(_routing.CurrentValue, asked);
            Report(violations, enforce, who, grant is not null, normalized, asked);
        }

        return new LlmCallerDecision(!(enforce && violations.Count > 0), violations);
    }

    // NFR-18, IADR-0702: 埋め込みの許可。表に無い呼び手（unknown_caller）と、表に在るが AllowEmbedding が偽の呼び手（embedding_not_allowed）。
    // 段は EmbeddingMode（Off は判定しない＝違反なしで通す）。
    public LlmCallerDecision EvaluateEmbedding(string? caller)
    {
        var o = _options.CurrentValue;
        if (o.EmbeddingMode == LlmGuardStage.Off)
            return new LlmCallerDecision(true, []);

        var who = string.IsNullOrWhiteSpace(caller) ? UnknownCaller : caller.Trim();
        var violations = new List<string>(1);
        if (!o.Callers.TryGetValue(who, out var grant))
            violations.Add(KindUnknownCaller);
        else if (!grant.AllowEmbedding)
            violations.Add(KindEmbeddingNotAllowed);

        var enforce = o.EmbeddingMode == LlmGuardStage.Enforce;
        if (violations.Count > 0)
            Report(violations, enforce, who, grant is not null, EmbeddingPurpose, EmbeddingPurpose);

        return new LlmCallerDecision(!(enforce && violations.Count > 0), violations);
    }

    // 呼び手は許可表にある名前（小文字）か other（値域を閉じる）。ログの本文には Sanitize した実名を出す。
    internal static string CallerLabel(LlmCallerPolicyOptions options, string? caller)
    {
        var who = string.IsNullOrWhiteSpace(caller) ? UnknownCaller : caller.Trim();
        return options.Callers.ContainsKey(who) ? who.ToLowerInvariant() : OtherCaller;
    }

    private void Report(
        List<string> violations, bool enforce, string who, bool known, string normalizedPurpose, string askedPurpose)
    {
        var mode = enforce ? "enforce" : "report";
        var callerLabel = known ? who.ToLowerInvariant() : OtherCaller;
        foreach (var kind in violations)
        {
            _violations.Add(1, new TagList
            {
                { KindTag, kind }, { ModeTag, mode }, { CallerTag, callerLabel }, { LlmCompletionMetrics.PurposeTag, normalizedPurpose },
            });
            if (_throttle.ShouldLog($"caller-policy|{kind}|{callerLabel}|{normalizedPurpose}", out var suppressed))
                _logger.LogWarning(
                    "LLM caller policy violation: kind={PolicyKind} mode={PolicyMode} caller={Caller} purpose={Purpose} "
                    + "suppressedSinceLast={Suppressed} (allowed={Allowed}; logged at most once per {Interval})",
                    kind, mode, LlmRouter.Sanitize(who), LlmRouter.Sanitize(askedPurpose), suppressed, !enforce,
                    LogOccurrenceThrottle.SummaryInterval);
        }
    }
}
