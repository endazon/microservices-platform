using System.Diagnostics.Metrics;
using AwesomeAssertions;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace LlmGateway.Tests.Domain.Authorization;

// NFR-02, NFR-18, NFR-21, IADR-0378, IADR-0702: 合成監視の標識を引き継いでよい呼び手の許可集合（Llm:SyntheticMarker）の単体試験。
[Collection(SharedMeterCollection.Name)]
[Trait("TestKind", "Unit")]
public sealed class LlmSyntheticMarkerPolicyTests : IDisposable
{
    private readonly ServiceProvider _meters = new ServiceCollection().AddMetrics().BuildServiceProvider();

    public void Dispose() => _meters.Dispose();

    private LlmSyntheticMarkerPolicy Policy(LlmSyntheticMarkerMode mode, params string[] trusted)
    {
        var callers = new LlmCallerPolicyOptions();
        callers.Callers["ast"] = new LlmCallerGrant { Purposes = ["trade-decision"] };
        return new LlmSyntheticMarkerPolicy(
            new TestClaudeProviders.StaticOptionsMonitor<LlmSyntheticMarkerOptions>(
                new LlmSyntheticMarkerOptions { Mode = mode, TrustedCallers = trusted }),
            new TestClaudeProviders.StaticOptionsMonitor<LlmCallerPolicyOptions>(callers),
            _meters.GetRequiredService<IMeterFactory>(),
            new LogOccurrenceThrottle(new FakeTimeProvider()),
            NullLogger<LlmSyntheticMarkerPolicy>.Instance);
    }

    [Fact]
    public void 段の既定はReportで_許可集合の既定は空である()
    {
        var o = new LlmSyntheticMarkerOptions();
        o.Mode.Should().Be(LlmSyntheticMarkerMode.Report);
        o.TrustedCallers.Should().BeEmpty();
    }

    [Theory]
    [InlineData(LlmSyntheticMarkerMode.Report)]
    [InlineData(LlmSyntheticMarkerMode.Enforce)]
    public void 標識が無ければ段に依らず合成ではない(LlmSyntheticMarkerMode mode)
    {
        Policy(mode, "aianalysis-service").IsSynthetic("aianalysis-service", markerPresent: false).Should().BeFalse();
        Policy(mode).IsSynthetic("ast", markerPresent: false).Should().BeFalse();
    }

    [Theory]
    [InlineData(LlmSyntheticMarkerMode.Report)]
    [InlineData(LlmSyntheticMarkerMode.Enforce)]
    public void 許可集合の呼び手の標識は段に依らず引き継ぐ_大文字小文字と前後の空白は問わない(LlmSyntheticMarkerMode mode)
    {
        var p = Policy(mode, " aianalysis-service ", "retrieval-service");

        p.IsSynthetic("AiAnalysis-Service", markerPresent: true).Should().BeTrue();
        p.IsSynthetic("retrieval-service", markerPresent: true).Should().BeTrue();
    }

    [Theory]
    [InlineData("ast")]
    [InlineData("stranger")]
    [InlineData(null)]
    public void 許可集合の外の呼び手の標識は段2で無視し_段1で引き継ぐ(string? caller)
    {
        Policy(LlmSyntheticMarkerMode.Enforce, "aianalysis-service").IsSynthetic(caller, markerPresent: true)
            .Should().BeFalse("段 2 は許可集合の外の標識を引き継がない（費用に計上する）");
        Policy(LlmSyntheticMarkerMode.Report, "aianalysis-service").IsSynthetic(caller, markerPresent: true)
            .Should().BeTrue("段 1 は従前どおり引き継ぐ（数えて報告するだけ）");
    }

    [Fact]
    public void 段2で許可集合が空ならどの標識も引き継がない()
        => Policy(LlmSyntheticMarkerMode.Enforce).IsSynthetic("aianalysis-service", markerPresent: true).Should().BeFalse();

    [Fact]
    public void 許可集合の外の標識を段と値域を閉じた呼び手で数える()
    {
        var seen = new List<Dictionary<string, string>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Name == LlmSyntheticMarkerPolicy.UntrustedCounterName) l.EnableMeasurementEvents(i);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var d = new Dictionary<string, string>();
            foreach (var t in tags) d[t.Key] = t.Value?.ToString() ?? "";
            lock (seen) seen.Add(d);
        });
        listener.Start();
        var p = Policy(LlmSyntheticMarkerMode.Enforce, "aianalysis-service");

        p.IsSynthetic("ast", markerPresent: true);
        p.IsSynthetic("stranger-1", markerPresent: true);
        p.IsSynthetic("aianalysis-service", markerPresent: true); // 許可集合の内は数えない

        lock (seen)
        {
            seen.Should().HaveCount(2);
            seen.Select(d => d[LlmCallerPolicy.CallerTag]).Should().Equal("ast", LlmCallerPolicy.OtherCaller);
            seen.Should().OnlyContain(d => d[LlmSyntheticMarkerPolicy.ModeTag] == "enforce");
        }
    }

    [Fact]
    public void 設定の検証は段の値と空の名前を落とす()
    {
        var v = new LlmSyntheticMarkerOptionsValidator();
        v.Validate(null, new LlmSyntheticMarkerOptions { TrustedCallers = ["aianalysis-service"] }).Succeeded.Should().BeTrue();
        v.Validate(null, new LlmSyntheticMarkerOptions { TrustedCallers = [" "] }).Failed.Should().BeTrue();
        v.Validate(null, new LlmSyntheticMarkerOptions { Mode = (LlmSyntheticMarkerMode)9 }).Failed.Should().BeTrue();
    }
}
