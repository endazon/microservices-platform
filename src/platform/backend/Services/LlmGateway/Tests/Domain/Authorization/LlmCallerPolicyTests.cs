using System.Diagnostics.Metrics;
using AwesomeAssertions;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Authorization;
using LlmGateway.Domain.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace LlmGateway.Tests.Domain.Authorization;

// NFR-18, IADR-0684 (#2417): 呼び手ごとの許可表の判定器の単体試験。
[Collection(SharedMeterCollection.Name)]
[Trait("TestKind", "Unit")]
public sealed class LlmCallerPolicyTests : IDisposable
{
    private readonly ServiceProvider _meters = new ServiceCollection().AddMetrics().BuildServiceProvider();

    public void Dispose() => _meters.Dispose();

    private LlmCallerPolicy Policy(
        LlmCallerPolicyMode mode, ILoggerFactory? loggers = null, LlmGuardStage embeddingMode = LlmGuardStage.Report)
    {
        var o = new LlmCallerPolicyOptions { Mode = mode, EmbeddingMode = embeddingMode };
        o.Callers["retrieval-service"] = new LlmCallerGrant { Purposes = ["rerank", "default"], AllowEmbedding = true };
        o.Callers["ast"] = new LlmCallerGrant { Purposes = ["trade-decision"], AllowModelOverride = true };
        var routing = new LlmRoutingOptions();
        routing.PurposeModels["rerank"] = "m";
        return new LlmCallerPolicy(
            new TestClaudeProviders.StaticOptionsMonitor<LlmCallerPolicyOptions>(o),
            new TestClaudeProviders.StaticOptionsMonitor<LlmRoutingOptions>(routing),
            _meters.GetRequiredService<IMeterFactory>(),
            new LogOccurrenceThrottle(new FakeTimeProvider()),
            loggers is null ? NullLogger<LlmCallerPolicy>.Instance : new Logger<LlmCallerPolicy>(loggers));
    }

    [Fact]
    public void 許可表にある呼び手と用途は違反なし_用途の大文字小文字と未指定はdefaultとして扱う()
    {
        var p = Policy(LlmCallerPolicyMode.Enforce);

        var d = p.Evaluate("retrieval-service", "RERANK", null);
        d.Allowed.Should().BeTrue();
        d.Violations.Should().BeEmpty();
        p.Evaluate("Retrieval-Service", null, null).Violations.Should().BeEmpty();
        p.Evaluate("ast", "trade-decision", "claude-sonnet-5-5").Violations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("wiki-service", "rerank", null, LlmCallerPolicy.KindUnknownCaller)]
    [InlineData(null, "rerank", null, LlmCallerPolicy.KindUnknownCaller)]
    [InlineData("retrieval-service", "analysis", null, LlmCallerPolicy.KindPurposeNotAllowed)]
    [InlineData("retrieval-service", "rerank-x", null, LlmCallerPolicy.KindPurposeNotAllowed)]
    [InlineData("retrieval-service", "rerank", "claude-opus-5-5", LlmCallerPolicy.KindModelOverride)]
    public void 許可表に無い呼び出しはEnforceで拒みReportで通す(string? caller, string purpose, string? model, string kind)
    {
        var enforced = Policy(LlmCallerPolicyMode.Enforce).Evaluate(caller, purpose, model);
        enforced.Allowed.Should().BeFalse();
        enforced.Violations.Should().Equal(kind);

        var reported = Policy(LlmCallerPolicyMode.Report).Evaluate(caller, purpose, model);
        reported.Allowed.Should().BeTrue();
        reported.Violations.Should().Equal(kind);
    }

    [Fact]
    public void 不正な設定は起動時に落とす()
    {
        var v = new LlmCallerPolicyOptionsValidator();
        var o = new LlmCallerPolicyOptions();
        o.Callers["x"] = new LlmCallerGrant { Purposes = [" "] };

        v.Validate(null, new LlmCallerPolicyOptions()).Succeeded.Should().BeTrue();
        v.Validate(null, o).Failed.Should().BeTrue();
        v.Validate(null, new LlmCallerPolicyOptions { Mode = (LlmCallerPolicyMode)9 }).Failed.Should().BeTrue();
    }

    // AI レビュー #2429: 許可表に無い呼び手の名前を何通り流しても、計器の系列と Warning の抑止の鍵は増えない（other に丸める）。
    [Fact]
    public void 許可表に無い呼び手は計器と抑止の鍵でotherに丸め_100通り流しても系列とログは増えない()
    {
        var callers = new HashSet<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Name == LlmCallerPolicy.ViolationCounterName) l.EnableMeasurementEvents(i);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var t in tags)
                if (t.Key == LlmCallerPolicy.CallerTag) lock (callers) callers.Add(t.Value?.ToString() ?? "");
        });
        listener.Start();
        using var loggers = new RecordingLoggerFactory();
        var p = Policy(LlmCallerPolicyMode.Report, loggers);

        for (var i = 0; i < 100; i++)
            p.Evaluate($"stranger-{i}", "rerank", null);
        p.Evaluate("retrieval-service", "analysis", null);

        lock (callers) callers.Should().BeEquivalentTo([LlmCallerPolicy.OtherCaller, "retrieval-service"]);
        var warnings = loggers.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        warnings.Should().HaveCount(2, "抑止の鍵は (種類, other, 用途) と (種類, retrieval-service, 用途) の 2 つだけ");
        warnings[0].Values["Caller"].Should().Be("stranger-0", "ログの本文には実名を出す");
    }

    // ---- NFR-18, IADR-0702: 埋め込みの許可（AllowEmbedding・段は EmbeddingMode） -------------------------------

    [Fact]
    public void 埋め込みの許可がある呼び手は違反なし_段の既定はReportである()
    {
        new LlmCallerPolicyOptions().EmbeddingMode.Should().Be(LlmGuardStage.Report);
        new LlmCallerGrant().AllowEmbedding.Should().BeFalse("既定は不可");

        var d = Policy(LlmCallerPolicyMode.Report, embeddingMode: LlmGuardStage.Enforce).EvaluateEmbedding("Retrieval-Service");
        d.Allowed.Should().BeTrue();
        d.Violations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("ast", LlmCallerPolicy.KindEmbeddingNotAllowed)]
    [InlineData("wiki-service", LlmCallerPolicy.KindUnknownCaller)]
    [InlineData(null, LlmCallerPolicy.KindUnknownCaller)]
    public void 埋め込みは許可の無い呼び手を段2で拒み段1で通し_Offは判定しない(string? caller, string kind)
    {
        // 補完の段（Mode）とは独立に切り替わる（補完が Report でも埋め込みの Enforce は拒む）。
        var enforced = Policy(LlmCallerPolicyMode.Report, embeddingMode: LlmGuardStage.Enforce).EvaluateEmbedding(caller);
        enforced.Allowed.Should().BeFalse();
        enforced.Violations.Should().Equal(kind);

        var reported = Policy(LlmCallerPolicyMode.Enforce, embeddingMode: LlmGuardStage.Report).EvaluateEmbedding(caller);
        reported.Allowed.Should().BeTrue("段 1 は通す");
        reported.Violations.Should().Equal(kind);

        var off = Policy(LlmCallerPolicyMode.Enforce, embeddingMode: LlmGuardStage.Off).EvaluateEmbedding(caller);
        off.Allowed.Should().BeTrue();
        off.Violations.Should().BeEmpty("Off は判定しない（従前の挙動）");
    }

    [Fact]
    public void 埋め込みの違反は用途embeddingと段を計器に載せる()
    {
        var seen = new List<Dictionary<string, string>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Name == LlmCallerPolicy.ViolationCounterName) l.EnableMeasurementEvents(i);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var d = new Dictionary<string, string>();
            foreach (var t in tags) d[t.Key] = t.Value?.ToString() ?? "";
            lock (seen) seen.Add(d);
        });
        listener.Start();

        Policy(LlmCallerPolicyMode.Report, embeddingMode: LlmGuardStage.Enforce).EvaluateEmbedding("ast");

        lock (seen)
            seen.Should().ContainSingle(d => d[LlmCallerPolicy.KindTag] == LlmCallerPolicy.KindEmbeddingNotAllowed)
                .Which.Should().Contain(new Dictionary<string, string>
                {
                    [LlmCallerPolicy.ModeTag] = "enforce",
                    [LlmCallerPolicy.CallerTag] = "ast",
                    [LlmCompletionMetrics.PurposeTag] = "embedding",
                });
    }
}
