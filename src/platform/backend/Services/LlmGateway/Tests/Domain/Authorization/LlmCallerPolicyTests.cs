using System.Diagnostics.Metrics;
using AwesomeAssertions;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Authorization;
using LlmGateway.Domain.Routing;
using Microsoft.Extensions.DependencyInjection;
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

    private LlmCallerPolicy Policy(LlmCallerPolicyMode mode)
    {
        var o = new LlmCallerPolicyOptions { Mode = mode };
        o.Callers["retrieval-service"] = new LlmCallerGrant { Purposes = ["rerank", "default"] };
        o.Callers["ast"] = new LlmCallerGrant { Purposes = ["trade-decision"], AllowModelOverride = true };
        var routing = new LlmRoutingOptions();
        routing.PurposeModels["rerank"] = "m";
        return new LlmCallerPolicy(
            new TestClaudeProviders.StaticOptionsMonitor<LlmCallerPolicyOptions>(o),
            new TestClaudeProviders.StaticOptionsMonitor<LlmRoutingOptions>(routing),
            _meters.GetRequiredService<IMeterFactory>(),
            new LogOccurrenceThrottle(new FakeTimeProvider()),
            NullLogger<LlmCallerPolicy>.Instance);
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
}
