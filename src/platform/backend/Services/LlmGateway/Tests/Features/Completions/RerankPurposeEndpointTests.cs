using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LlmGateway.Tests.Features.Completions;

// ［2026-10-10 追記 / #1875・IADR-0531］利用者裁定（planning#783）で Claude の割当を 5.5 系へ切り替えたので、
// 本ファイルが渡す・期待するモデル名（コード）を claude-opus-5-5 / claude-sonnet-5-5 / claude-haiku-5-5 へ改めた。
// **コメント中の旧モデル名（claude-opus-5 / claude-sonnet-5 / claude-haiku-4-5）は当時の決定の記録**であり、書き換えていない。

// T-28, FR-03, FR-10, FR-11, ADR-0127 決定 3, ADR-0044 決定 1, ADR-0038 決定 3・5, [[IADR-0498]] 決定 6 (#1746 段 S2):
// **用途 `rerank`（検索結果の再順位付け）は本番の設定で軽量モデルへ解決し、費用は回答生成と分けて積まれる。**
//
// 🔴 陽性と陰性を対で置く —— `rerank` の計上が `rerank` の軸に載ること（陽性）と、同じ購読で
// `rag-answer` が別の軸に載ること（対照）。用途を登録し忘れると計器は `other` へ集約し、回答生成と分けられない。
[Collection(SharedMeterCollection.Name)]
[Trait("TestKind", "Integration")]
public class RerankPurposeEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private record CompletionResponse(
        string Text, string Model, int InputTokens, int OutputTokens,
        bool Sent, string? Endpoint, string? RoutingReason);

    private sealed record Measured(string Instrument, double Value, IReadOnlyDictionary<string, string> Tags);

    // 費用系の計器（LlmUsageMetrics の Meter）だけを、**この容器の Meter インスタンス**で購読する（[[IADR-0394]] 決定 1）。
    private sealed class UsageProbe : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<Measured> _items = [];

        public UsageProbe(IServiceProvider services)
        {
            _ = services.GetRequiredService<LlmUsageMetrics>();
            var meter = services.GetRequiredService<IMeterFactory>().Create(LlmUsageMetrics.MeterName);
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (ReferenceEquals(instrument.Meter, meter)
                        && instrument.Name is LlmUsageMetrics.TokensCounterName or LlmUsageMetrics.CostCounterName)
                        l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Add(i.Name, v, tags));
            _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Add(i.Name, v, tags));
            _listener.Start();
        }

        private void Add(string name, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var dict = new Dictionary<string, string>();
            foreach (var tag in tags) dict[tag.Key] = tag.Value?.ToString() ?? string.Empty;
            lock (_items) _items.Add(new Measured(name, value, dict));
        }

        public IReadOnlyList<Measured> Items { get { lock (_items) return [.. _items]; } }
        public void Dispose() => _listener.Dispose();
    }

    // T-28: 本番の設定で `rerank` は claude-haiku-4-5 へ解決し、DefaultModel（claude-opus-5）へ無音で落ちない。
    // 再順位付けの候補は restricted（と未指定・未知）を含み得るので、区分を上げても同じモデルで送れること。
    [Theory]
    [InlineData("public")]
    [InlineData("confidential")]
    [InlineData("restricted")]
    public async Task PostComplete_Rerank_SelectsHaiku55AcrossSensitivities(string confidentiality)
    {
        var req = new { Prompt = "並べ替え", MaxTokens = 512, Confidentiality = confidentiality, Purpose = "rerank" };
        var response = await factory.CreateClient().PostAsJsonAsync("/complete", req, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>(TestContext.Current.CancellationToken);
        body!.Sent.Should().BeTrue();
        body.Endpoint.Should().Be("claude-managed");
        body.Model.Should().Be("claude-haiku-5-5");
    }

    // T-28, ADR-0038 決定 3: `rerank` は鎖を持たない（最安のモデルからさらに安い先が無い。鎖は安価側へ向かう）。
    // 失敗は検索サービスの段が元の順へ戻す（[[IADR-0498]] 決定 7）。
    [Fact]
    public void Rerank_IsRegisteredWithoutFallbackChainInProductionConfig()
    {
        var options = factory.Services.GetRequiredService<IOptions<LlmRoutingOptions>>().Value;

        options.PurposeModels.Should().ContainKey("rerank");
        options.PurposeModels["rerank"].Should().Be("claude-haiku-5-5");
        options.PurposeFallbackModels.Should().NotContainKey("rerank");
    }

    // T-28, ADR-0044 決定 1・3: **費用は用途 `rerank` の軸に、回答生成（`rag-answer`）と分けて積まれる。**
    [Fact]
    public async Task PostComplete_Rerank_IsMeteredUnderItsOwnPurpose()
    {
        var client = factory.CreateClient();
        using var probe = new UsageProbe(factory.Services);

        (await client.PostAsJsonAsync("/complete",
            new { Prompt = "並べ替え", MaxTokens = 512, Confidentiality = "restricted", Purpose = "rerank" },
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/complete",
            new { Prompt = "回答", MaxTokens = 512, Confidentiality = "restricted", Purpose = "rag-answer" },
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var rerank = probe.Items.Where(m => m.Tags[LlmCompletionMetrics.PurposeTag] == "rerank").ToList();
        rerank.Should().Contain(m => m.Instrument == LlmUsageMetrics.TokensCounterName
            && m.Tags[LlmCompletionMetrics.ModelTag] == "claude-haiku-5-5");
        rerank.Should().Contain(m => m.Instrument == LlmUsageMetrics.CostCounterName && m.Value > 0,
            "claude-haiku-5-5 は単価表にあるので金額へ換算される");
        probe.Items.Should().Contain(m => m.Tags[LlmCompletionMetrics.PurposeTag] == "rag-answer",
            "対照: 回答生成は別の軸に載る");
        probe.Items.Should().NotContain(m => m.Tags[LlmCompletionMetrics.PurposeTag] == "other",
            "rerank が未登録なら other へ集約され、回答生成と分けられない");
    }
}
