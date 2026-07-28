using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using FluentAssertions;
using LlmGateway.Api.Foundation.Observability;
using LlmGateway.Api.Foundation.Ports;
using LlmGateway.Api.Foundation.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Shared.Contracts.Dtos;

namespace LlmGateway.Api.Tests;

// T-21, FR-11, NFR, IADR-0110 (#395): 補完の終了理由がメトリクスとして計上され、
// 「送信していない（越境拒否）」と「送ったがモデルが拒否した（refusal）」が別軸で区別できることを固定する。
// 以前は終了理由がログにしか出ず、拒否率を継続的に把握する手段が無かった（IADR-0104 §フォローアップ 3）。
//
// MeterListener は Meter 名でプロセス全体の測定を購読するため、他のテストクラスが並行して /complete を
// 叩くと測定が混入する。補完エンドポイントを叩くテストクラスを 1 コレクションへまとめて直列化する。
[Collection(CompletionEndpointCollection.Name)]
public class CompletionMetricsTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    // 属性のカーディナリティを閉じることが本メトリクスの要点であるため、テストも属性値で検証する。
    private sealed record Measurement(long Value, Dictionary<string, string> Tags);

    // llm.completion.total の測定を収集するリスナー（テストの寿命だけ購読する）。
    private sealed class MetricsProbe : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<Measurement> _measurements = [];

        public MetricsProbe()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == LlmCompletionMetrics.MeterName
                        && instrument.Name == LlmCompletionMetrics.CompletionCounterName)
                        l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                var dict = new Dictionary<string, string>();
                foreach (var tag in tags)
                    dict[tag.Key] = tag.Value?.ToString() ?? string.Empty;
                lock (_measurements)
                    _measurements.Add(new Measurement(value, dict));
            });
            _listener.Start();
        }

        public IReadOnlyList<Measurement> Measurements
        {
            get { lock (_measurements) return [.. _measurements]; }
        }

        public void Dispose() => _listener.Dispose();
    }

    private HttpClient ClientReturning(CompletionResult result) =>
        factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ILlmProvider>();
                var provider = new FixedResultProvider(result);
                s.AddKeyedSingleton<ILlmProvider>("claude", provider);
                s.AddKeyedSingleton<ILlmProvider>("selfhosted", provider);
                s.AddKeyedSingleton<ILlmProvider>("copilot", provider);
            })).CreateClient();

    private static object Request(string purpose, string confidentiality = "public") =>
        new { Prompt = "要求", MaxTokens = 100, Confidentiality = confidentiality, Purpose = purpose };

    // T-21a: 拒否は「送信成立（result=sent）」かつ「stop_reason=refusal」として計上される。
    // 拒否率の分子はこの系列、分母は result=sent の総和である。
    [Fact]
    public async Task PostComplete_WhenRefusal_CountsSentWithRefusalStopReason()
    {
        using var probe = new MetricsProbe();
        var client = ClientReturning(new CompletionResult("", 11, 0, CompletionStopReasons.Refusal));

        await client.PostAsJsonAsync("/complete", Request("rag-answer"));

        var m = probe.Measurements.Should().ContainSingle().Subject;
        m.Value.Should().Be(1);
        m.Tags[LlmCompletionMetrics.ResultTag].Should().Be(LlmCompletionMetrics.ResultSent);
        m.Tags[LlmCompletionMetrics.StopReasonTag].Should().Be(CompletionStopReasons.Refusal);
        m.Tags[LlmCompletionMetrics.PurposeTag].Should().Be("rag-answer");
        m.Tags[LlmCompletionMetrics.ConfidentialityTag].Should().Be("public");
    }

    // T-21b: 上限到達は拒否と混ざらない（本文が空になる点は同じでも原因・対処が異なる）。
    [Fact]
    public async Task PostComplete_WhenMaxTokens_CountsMaxTokensStopReason()
    {
        using var probe = new MetricsProbe();
        var client = ClientReturning(new CompletionResult("", 11, 100, CompletionStopReasons.MaxTokens));

        await client.PostAsJsonAsync("/complete", Request("rag-answer"));

        probe.Measurements.Should().ContainSingle()
            .Which.Tags[LlmCompletionMetrics.StopReasonTag].Should().Be(CompletionStopReasons.MaxTokens);
    }

    // T-21c: 正常終了（分母に入るが分子には入らない）。
    [Fact]
    public async Task PostComplete_WhenEndTurn_CountsEndTurnStopReason()
    {
        using var probe = new MetricsProbe();
        var client = ClientReturning(new CompletionResult("回答本文", 11, 22, CompletionStopReasons.EndTurn));

        await client.PostAsJsonAsync("/complete", Request("rag-answer"));

        var m = probe.Measurements.Should().ContainSingle().Subject;
        m.Tags[LlmCompletionMetrics.ResultTag].Should().Be(LlmCompletionMetrics.ResultSent);
        m.Tags[LlmCompletionMetrics.StopReasonTag].Should().Be(CompletionStopReasons.EndTurn);
    }

    // T-21f: 未知の終了理由は other へ集約する（カーディナリティを閉じる。原文はログ側が保持する）。
    [Fact]
    public async Task PostComplete_WhenUnknownStopReason_CollapsesToOther()
    {
        using var probe = new MetricsProbe();
        var client = ClientReturning(new CompletionResult("本文", 11, 22, "some_future_reason"));

        await client.PostAsJsonAsync("/complete", Request("rag-answer"));

        probe.Measurements.Should().ContainSingle()
            .Which.Tags[LlmCompletionMetrics.StopReasonTag].Should().Be(LlmCompletionMetrics.ValueOther);
    }

    // T-21g: 未定義の purpose は other へ集約する。purpose は呼び出し側の自由文字列であり、
    // 素通しすると誤設定 1 つで系列が無限に増える。
    [Fact]
    public async Task PostComplete_WhenUndefinedPurpose_CollapsesToOther()
    {
        using var probe = new MetricsProbe();
        var client = ClientReturning(new CompletionResult("本文", 11, 22, CompletionStopReasons.EndTurn));

        await client.PostAsJsonAsync("/complete", Request("experimental-purpose-xyz"));

        probe.Measurements.Should().ContainSingle()
            .Which.Tags[LlmCompletionMetrics.PurposeTag].Should().Be(LlmCompletionMetrics.ValueOther);
    }

    // T-21d: 越境拒否（送信していない）は result=egress_denied・stop_reason=none。
    // モデルの refusal と同じ系列に混ざらないことが要点（IADR-0104 の軸の分離）。
    [Fact]
    public async Task PostComplete_WhenEgressDenied_CountsDeniedWithoutStopReason()
    {
        using var probe = new MetricsProbe();
        var client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
                s.Configure<LlmRoutingOptions>(o =>
                {
                    o.AllowUnapprovedTierC = false;
                    o.Endpoints =
                    [
                        new LlmEndpointOptions
                        {
                            Name = "standard-external", Tier = ProtectionTier.C,
                            Provider = "claude", Enabled = true, Priority = 1,
                            DefaultModel = "std", Models = ["std"]
                        }
                    ];
                }))).CreateClient();

        await client.PostAsJsonAsync("/complete", Request("analysis", "confidential"));

        var m = probe.Measurements.Should().ContainSingle().Subject;
        m.Tags[LlmCompletionMetrics.ResultTag].Should().Be(LlmCompletionMetrics.ResultEgressDenied);
        m.Tags[LlmCompletionMetrics.StopReasonTag].Should().Be(LlmCompletionMetrics.ValueNone);
        m.Tags[LlmCompletionMetrics.ConfidentialityTag].Should().Be("confidential");
    }

    // T-21e: 呼び出し先が不調（例外）は upstream_error として計上し、拒否・越境拒否と切り分ける。
    [Fact]
    public async Task PostComplete_WhenProviderThrows_CountsUpstreamError()
    {
        using var probe = new MetricsProbe();
        var client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ILlmProvider>();
                var provider = new ThrowingProvider();
                s.AddKeyedSingleton<ILlmProvider>("claude", provider);
                s.AddKeyedSingleton<ILlmProvider>("selfhosted", provider);
                s.AddKeyedSingleton<ILlmProvider>("copilot", provider);
            })).CreateClient();

        await client.PostAsJsonAsync("/complete", Request("rag-answer"));

        var m = probe.Measurements.Should().ContainSingle().Subject;
        m.Tags[LlmCompletionMetrics.ResultTag].Should().Be(LlmCompletionMetrics.ResultUpstreamError);
        m.Tags[LlmCompletionMetrics.StopReasonTag].Should().Be(LlmCompletionMetrics.ValueNone);
    }

    // T-21h: ストリーミング経路（IADR-0037）も同じ属性で計上する（経路によって観測が欠けない）。
    [Fact]
    public async Task PostCompleteStream_WhenRefusal_CountsSentWithRefusalStopReason()
    {
        using var probe = new MetricsProbe();
        var client = ClientReturning(new CompletionResult("", 11, 0, CompletionStopReasons.Refusal));

        await client.PostAsJsonAsync("/complete/stream", Request("rag-answer"));

        var m = probe.Measurements.Should().ContainSingle().Subject;
        m.Tags[LlmCompletionMetrics.ResultTag].Should().Be(LlmCompletionMetrics.ResultSent);
        m.Tags[LlmCompletionMetrics.StopReasonTag].Should().Be(CompletionStopReasons.Refusal);
    }

    private sealed class FixedResultProvider(CompletionResult result) : ILlmProvider
    {
        public Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
            => Task.FromResult(result);
    }

    private sealed class ThrowingProvider : ILlmProvider
    {
        public Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
            => throw new HttpRequestException("upstream is down");
    }
}
