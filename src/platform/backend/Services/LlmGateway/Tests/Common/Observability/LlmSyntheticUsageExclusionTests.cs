using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using AwesomeAssertions;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Ports;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace LlmGateway.Tests.Common.Observability;

// NFR-02, FR-10, ADR-0044, ADR-0076 決定 4, [[IADR-0378]] (#1203):
// **合成監視の補完は LLM 費用計測（ADR-0044）へ計上しない。**
//
// 🔴 **陽性と陰性を対で置く。** 「合成は外れる」だけでは、計器がそもそも動いていなくても緑になる。
// 対にした陰性（標識なしは計上される）が、**同じ経路・同じ購読で確かに載る**ことの陽性対照である。
//
// 🔴 **黙って落とさないこと**も併せて固定する —— 除外した件数が
// `llm.usage.synthetic_excluded.total` に載っていなければ、「合成だけが通っていて実利用は 0」でも
// 費用ダッシュボードが平常に見える。
//
// MeterListener は Meter 名でプロセス全体の測定を購読するため、補完エンドポイントを叩く
// テストクラスと直列化する（CompletionMetricsTests と同じ理由・同じコレクション）。
[Collection(CompletionEndpointCollection.Name)]
[Trait("TestKind", "Integration")]
public class LlmSyntheticUsageExclusionTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private sealed record Measured(string Instrument, double Value);

    // 費用系の計器（LlmUsageMetrics の Meter）だけを購読する。
    private sealed class UsageProbe : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<Measured> _items = [];

        public UsageProbe()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == LlmUsageMetrics.MeterName
                        && instrument.Name is LlmUsageMetrics.TokensCounterName
                            or LlmUsageMetrics.CostCounterName
                            or LlmUsageMetrics.SyntheticExcludedCounterName)
                        l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((i, v, _, _) => Add(i.Name, v));
            _listener.SetMeasurementEventCallback<double>((i, v, _, _) => Add(i.Name, v));
            _listener.Start();
        }

        private void Add(string name, double value)
        {
            lock (_items) _items.Add(new Measured(name, value));
        }

        public IReadOnlyList<Measured> Items { get { lock (_items) return [.. _items]; } }
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

    private static readonly object Request =
        new { Prompt = "監視用の代表リクエスト", MaxTokens = 100, Confidentiality = "public", Purpose = "default" };

    // ★ 陰性対照: 標識が無い補完は従来どおり費用（トークン累計）へ計上される。
    [Fact]
    public async Task PostComplete_WhenNotSynthetic_RecordsUsageTokens()
    {
        using var probe = new UsageProbe();
        var client = ClientReturning(new CompletionResult("回答本文", 11, 22, CompletionStopReasons.EndTurn));

        var response = await client.PostAsJsonAsync("/complete", Request, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        probe.Items.Should().Contain(m => m.Instrument == LlmUsageMetrics.TokensCounterName,
            "標識の無い呼び出しは ADR-0044 の費用計測に入る");
        probe.Items.Should().NotContain(m => m.Instrument == LlmUsageMetrics.SyntheticExcludedCounterName);
    }

    // 🔴 ★ 陽性: 標識つきの補完は費用へ計上せず、除外を別の計器へ積む。
    // **変異試験の対象**: `Complete/Endpoint.cs` の `if (isSynthetic)` を消すとここが落ちる。
    [Fact]
    public async Task PostComplete_WhenSynthetic_ExcludesFromCostAndCountsExclusion()
    {
        using var probe = new UsageProbe();
        var client = ClientReturning(new CompletionResult("回答本文", 11, 22, CompletionStopReasons.EndTurn));
        client.DefaultRequestHeaders.Add(SyntheticTraffic.HeaderName, SyntheticTraffic.HeaderValue);

        var response = await client.PostAsJsonAsync("/complete", Request, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        probe.Items.Should().NotContain(m => m.Instrument == LlmUsageMetrics.TokensCounterName,
            "合成監視は ADR-0044 の費用計測から外す（決定 4）");
        probe.Items.Should().NotContain(m => m.Instrument == LlmUsageMetrics.CostCounterName);
        probe.Items.Should().ContainSingle(m => m.Instrument == LlmUsageMetrics.SyntheticExcludedCounterName)
            .Which.Value.Should().Be(1, "**黙って落とさない** —— 外した件数を数える");
    }

    // ★ 陽性（SSE）: SC-01 が実際に使う経路でも同じ規則が効く。
    [Fact]
    public async Task PostCompleteStream_WhenSynthetic_ExcludesFromCostAndCountsExclusion()
    {
        using var probe = new UsageProbe();
        var client = ClientReturning(new CompletionResult("回答本文", 11, 22, CompletionStopReasons.EndTurn));
        client.DefaultRequestHeaders.Add(SyntheticTraffic.HeaderName, SyntheticTraffic.HeaderValue);

        var response = await client.PostAsJsonAsync(
            "/complete/stream",
            new { Prompt = "監視用の代表リクエスト", MaxTokens = 100, Confidentiality = "public", Purpose = "rag-answer" },
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        probe.Items.Should().NotContain(m => m.Instrument == LlmUsageMetrics.TokensCounterName);
        probe.Items.Should().ContainSingle(m => m.Instrument == LlmUsageMetrics.SyntheticExcludedCounterName);
    }

    private sealed class FixedResultProvider(CompletionResult result) : ILlmProvider
    {
        public Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
            => Task.FromResult(result);
    }
}
