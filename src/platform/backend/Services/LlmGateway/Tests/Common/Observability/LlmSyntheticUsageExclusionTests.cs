using System.Diagnostics;
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
// [[IADR-0394]] (#1275): MeterListener は Meter 名でプロセス全体の測定を購読するため、
// **購読は Meter の「インスタンス」で絞る**（自分のアプリの容器から解決したものだけ）。
// 直列化（SharedMeterCollection）は多層防御として併用するが、主はこちらである。
[Collection(SharedMeterCollection.Name)]
[Trait("TestKind", "Integration")]
public class LlmSyntheticUsageExclusionTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private sealed record Measured(string Instrument, double Value, IReadOnlyDictionary<string, string> Tags);

    // 費用系の計器（LlmUsageMetrics の Meter）だけを購読する。
    private sealed class UsageProbe : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<Measured> _items = [];

        public UsageProbe(IServiceProvider services)
        {
            // 🔴 **計器の生成を先に済ませる。** LlmUsageMetrics は singleton であり、解決するまで
            // カウンタが存在しない。存在しない計器は InstrumentPublished に載らず、
            // **購読しているつもりで何も見ていない**状態になる。
            _ = services.GetRequiredService<LlmUsageMetrics>();
            // 🔴 **Meter 名ではなくインスタンスで絞る（[[IADR-0394]] 決定 1）。**
            // IMeterFactory は容器ごとに別の Meter を作るので、他のテストクラスが
            // 同じ Meter 名（production の定数）へ発行しても、ここには入らない。
            var meter = services.GetRequiredService<IMeterFactory>().Create(LlmUsageMetrics.MeterName);

            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (ReferenceEquals(instrument.Meter, meter)
                        && instrument.Name is LlmUsageMetrics.TokensCounterName
                            or LlmUsageMetrics.CostCounterName
                            or LlmUsageMetrics.SyntheticExcludedCounterName)
                        l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Add(i.Name, v, tags));
            _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Add(i.Name, v, tags));
            _listener.Start();
        }

        // 値だけでなく**タグも保持する**（[[IADR-0394]] 決定 2）。混入が起きたときに
        // 「どの用途・どのモデルの発行か」が失敗メッセージから読める。
        private void Add(string name, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var dict = new Dictionary<string, string>();
            foreach (var tag in tags) dict[tag.Key] = tag.Value?.ToString() ?? string.Empty;
            lock (_items) _items.Add(new Measured(name, value, dict));
        }

        public IReadOnlyList<Measured> Items { get { lock (_items) return [.. _items]; } }
        public void Dispose() => _listener.Dispose();
    }

    private WebApplicationFactory<Program> AppReturning(CompletionResult result) =>
        factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ILlmProvider>();
                var provider = new FixedResultProvider(result);
                s.AddKeyedSingleton<ILlmProvider>("claude", provider);
                s.AddKeyedSingleton<ILlmProvider>("selfhosted", provider);
                s.AddKeyedSingleton<ILlmProvider>("copilot", provider);
            }));

    private static readonly object Request =
        new { Prompt = "監視用の代表リクエスト", MaxTokens = 100, Confidentiality = "public", Purpose = "default" };

    // ★ 陰性対照: 標識が無い補完は従来どおり費用（トークン累計）へ計上される。
    // **絞り込みが「何も拾わない」に退化していないことの常設の対照でもある** ——
    // インスタンス絞りが外れて空になれば、この Contain がまず落ちる。
    [Fact]
    public async Task PostComplete_WhenNotSynthetic_RecordsUsageTokens()
    {
        var app = AppReturning(new CompletionResult("回答本文", 11, 22, CompletionStopReasons.EndTurn));
        var client = app.CreateClient();
        using var probe = new UsageProbe(app.Services);

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
        var app = AppReturning(new CompletionResult("回答本文", 11, 22, CompletionStopReasons.EndTurn));
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(SyntheticTraffic.HeaderName, SyntheticTraffic.HeaderValue);
        using var probe = new UsageProbe(app.Services);

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
        var app = AppReturning(new CompletionResult("回答本文", 11, 22, CompletionStopReasons.EndTurn));
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(SyntheticTraffic.HeaderName, SyntheticTraffic.HeaderValue);
        using var probe = new UsageProbe(app.Services);

        var response = await client.PostAsJsonAsync(
            "/complete/stream",
            new { Prompt = "監視用の代表リクエスト", MaxTokens = 100, Confidentiality = "public", Purpose = "rag-answer" },
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        probe.Items.Should().NotContain(m => m.Instrument == LlmUsageMetrics.TokensCounterName);
        probe.Items.Should().ContainSingle(m => m.Instrument == LlmUsageMetrics.SyntheticExcludedCounterName);
    }

    // 🔴 NFR, ADR-0006, [[IADR-0394]] (#1275): **不在の表明を守る購読の絞り込み自体の回帰試験。**
    //
    // 上の 3 件が破れた原因は「合成の除外」ではなく **probe が他クラスの測定を拾ったこと**だった
    // （`LlmUsageMetricsTests` が同じ Meter 名へ `llm.tokens.total` を発行する。並列度を上げると 5/5 で再現）。
    // ここでは**その混入を人工的に再現する** —— 別の容器の IMeterFactory から
    // **同じ Meter 名・同じ計器名**で発行し、拾わないことを固定する。
    //
    // **陽性と陰性を対で置く。** 同じ probe が自分のアプリの発行は拾う（陽性）ので、
    // 「拾わない」は購読が死んでいるからではない。
    // **変異試験**: `ReferenceEquals(instrument.Meter, meter)` を
    // `instrument.Meter.Name == LlmUsageMetrics.MeterName` に戻すと、この試験が落ちる。
    [Fact]
    public async Task UsageProbe_IgnoresSameNamedMeterFromAnotherContainer()
    {
        var app = AppReturning(new CompletionResult("回答本文", 11, 22, CompletionStopReasons.EndTurn));
        var client = app.CreateClient();
        using var probe = new UsageProbe(app.Services);

        // ── 陰性: 別の容器から、同じ Meter 名・同じ計器名で発行する（他テストクラスの模倣）。
        using var otherProvider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var otherMeter = otherProvider.GetRequiredService<IMeterFactory>()
            .Create(LlmUsageMetrics.MeterName);
        var intruder = otherMeter.CreateCounter<long>(LlmUsageMetrics.TokensCounterName, unit: "{token}");
        intruder.Add(999_999, new TagList
        {
            { LlmCompletionMetrics.PurposeTag, "他クラスの用途" },
            { LlmUsageMetrics.TokenTypeTag, LlmUsageMetrics.TokenTypeInput },
        });

        // ── 陽性: 自分のアプリの発行は同じ probe が拾う。
        var response = await client.PostAsJsonAsync("/complete", Request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        probe.Items.Should().Contain(m => m.Instrument == LlmUsageMetrics.TokensCounterName,
            "★ 陽性対照 —— 拾わないのは購読が死んでいるからではない");
        probe.Items.Should().NotContain(m => m.Value == 999_999,
            "★ 同じ Meter 名でも別インスタンスの発行は入らない（#1275 の再発防止）");
        probe.Items.Should().NotContain(
            m => m.Tags.ContainsKey(LlmCompletionMetrics.PurposeTag)
                && m.Tags[LlmCompletionMetrics.PurposeTag] == "他クラスの用途",
            "タグを保持しているので、混入は用途からも判別できる");
    }

    private sealed class FixedResultProvider(CompletionResult result) : ILlmProvider
    {
        public Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
            => Task.FromResult(result);
    }
}
