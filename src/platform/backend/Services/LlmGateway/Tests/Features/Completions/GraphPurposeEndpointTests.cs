using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmGateway.Tests.Features.Completions;

// ［2026-10-10 追記 / #1875・IADR-0531］利用者裁定（planning#783）で Claude の割当を 5.5 系へ切り替えたので、
// 本ファイルが渡す・期待するモデル名（コード）を claude-opus-5-5 / claude-sonnet-5-5 / claude-haiku-5-5 へ改めた。
// **コメント中の旧モデル名（claude-opus-5 / claude-sonnet-5 / claude-haiku-4-5）は当時の決定の記録**であり、書き換えていない。

// T-30, FR-10, FR-11, FR-17, FR-18, ADR-0081 決定 3・フォローアップ 2, ADR-0044 決定 1, ADR-0035 決定 3,
// ADR-0038 決定 3・5, [[IADR-0511]] (#1785):
// **グラフサービスの 2 用途（AI 提案 `graph-suggestion`・クラスタ要約 `graph-cluster-summary`）は、
// 本番の設定で割り当てたモデルへ解決し、費用は用途名の軸に積まれる。**
//
// 登録が無かった間は、ゲートウェイは既定モデル（`claude-opus-5`・最も高い単価）へ落ち、計器は用途を `other` へ
// 丸めていた —— 提案生成の費用を他の未登録の用途と切り分けられなかった（ADR-0081 フォローアップ 2）。
//
// 🔴 **実 appsettings.json を読む器で確かめる**（`rerank` の T-28 と同型）。合成 config の試験は仕組みを固定するが、
// 設定に用途を書き忘れたことは捕まえない。
[Collection(SharedMeterCollection.Name)]
[Trait("TestKind", "Integration")]
public class GraphPurposeEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private const string Suggestion = "graph-suggestion";
    private const string ClusterSummary = "graph-cluster-summary";

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

    private LlmRoutingOptions Deployed()
        => factory.Services.GetRequiredService<IOptions<LlmRoutingOptions>>().Value;

    // 実設定の写しを作り、エンドポイントの DefaultModel だけを差し替える（他は実設定のまま）。
    private static LlmRoutingOptions WithDefaultModel(LlmRoutingOptions deployed, string defaultModel) => new()
    {
        AllowUnapprovedTierC = deployed.AllowUnapprovedTierC,
        PurposeModels = new(deployed.PurposeModels, StringComparer.OrdinalIgnoreCase),
        PurposeFallbackModels = new(
            deployed.PurposeFallbackModels.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
            StringComparer.OrdinalIgnoreCase),
        Endpoints = [.. deployed.Endpoints.Select(e => new LlmEndpointOptions
        {
            Name = e.Name,
            Tier = e.Tier,
            Provider = e.Provider,
            Models = [.. e.Models],
            NonZdrModels = [.. e.NonZdrModels],
            DefaultModel = defaultModel,
            Enabled = e.Enabled,
            Priority = e.Priority,
        })],
    };

    // T-30 ①, [[IADR-0511]] 決定 1・2: 本番の設定で 2 用途は割り当てたモデルへ解決する。
    // 提案生成は選別の仕事なので claude-sonnet-5（既定 claude-opus-5 でない）。クラスタ要約は ADR-0035 決定 3 の claude-opus-5。
    // 提案は封の最高区分・要約は封の区分を名乗るので、どの区分でも同じモデルで送れること（3 モデルとも ZDR 対応）。
    [Theory]
    [InlineData(Suggestion, "public", "claude-sonnet-5-5")]
    [InlineData(Suggestion, "internal", "claude-sonnet-5-5")]
    [InlineData(Suggestion, "confidential", "claude-sonnet-5-5")]
    [InlineData(Suggestion, "restricted", "claude-sonnet-5-5")]
    [InlineData(ClusterSummary, "public", "claude-opus-5-5")]
    [InlineData(ClusterSummary, "internal", "claude-opus-5-5")]
    [InlineData(ClusterSummary, "confidential", "claude-opus-5-5")]
    [InlineData(ClusterSummary, "restricted", "claude-opus-5-5")]
    public async Task PostComplete_GraphPurpose_SelectsAssignedModelAcrossSensitivities(
        string purpose, string confidentiality, string expectedModel)
    {
        var req = new { Prompt = "提案または要約", MaxTokens = 512, Confidentiality = confidentiality, Purpose = purpose };
        var response = await factory.CreateClient().PostAsJsonAsync("/complete", req, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>(TestContext.Current.CancellationToken);
        body!.Sent.Should().BeTrue();
        body.Endpoint.Should().Be("claude-managed");
        body.Model.Should().Be(expectedModel);
    }

    // T-30 ②, [[IADR-0511]] 決定 1: 提案生成は既定モデル（実設定の claude-managed.DefaultModel）へ落ちない。
    [Fact]
    public void Route_GraphSuggestion_DoesNotFallToEndpointDefaultModel()
    {
        var deployed = Deployed();
        var claude = deployed.Endpoints.Single(e => e.Name == "claude-managed");
        var router = new LlmRouter(Options.Create(deployed), NullLogger<LlmRouter>.Instance);

        var decision = router.Route(new RoutingRequest(SensitivityClass.Internal, Suggestion));

        decision.Allowed.Should().BeTrue();
        decision.Model.Should().Be("claude-sonnet-5-5");
        decision.Model.Should().NotBe(claude.DefaultModel, "未登録の用途は DefaultModel（最も高い単価）へ落ちる");
    }

    // T-30 ②, [[IADR-0511]] 決定 2, ADR-0035 決定 3: クラスタ要約の割当（claude-opus-5）は**現行の既定と同値**である。
    // 同値のままでは「割当が効いた」と「既定へ落ちた」を区別できないので、**既定だけを別のモデルへ差し替えた実設定**で
    // ルーターを組み、なお claude-opus-5 が選ばれることで割当が効いていることを示す（未登録なら差し替えた既定が出る）。
    // 提案生成も同じ形で確かめる（既定の改定に無音で追随しないこと。IADR-0112 決定 1）。
    [Theory]
    [InlineData(Suggestion, "claude-sonnet-5-5")]
    [InlineData(ClusterSummary, "claude-opus-5-5")]
    public void Route_GraphPurpose_ResolvesViaPurposeModelsEvenWhenDefaultModelChanges(string purpose, string expectedModel)
    {
        var options = WithDefaultModel(Deployed(), "claude-haiku-5-5");
        var router = new LlmRouter(Options.Create(options), NullLogger<LlmRouter>.Instance);

        var decision = router.Route(new RoutingRequest(SensitivityClass.Confidential, purpose));

        decision.Allowed.Should().BeTrue();
        decision.EndpointName.Should().Be("claude-managed");
        decision.Model.Should().Be(expectedModel, "PurposeModels の割当が選ばれ、差し替えた DefaultModel へは落ちない");
    }

    // T-30 ③, ADR-0038 決定 3・5, [[IADR-0511]] 決定 1・2: 鎖は 1 段下位・安価側へ向かい、ルーターが実際に返す
    // （鎖の要素が Models 未登録・ZDR 不適格なら warn を出して落とされ、空になる）。
    [Theory]
    [InlineData(Suggestion, "claude-haiku-5-5")]
    [InlineData(ClusterSummary, "claude-sonnet-5-5")]
    public void Route_GraphPurpose_CarriesOneStepCheaperFallback(string purpose, string expectedFallback)
    {
        var deployed = Deployed();
        deployed.PurposeFallbackModels.Should().ContainKey(purpose);
        var router = new LlmRouter(Options.Create(deployed), NullLogger<LlmRouter>.Instance);

        var decision = router.Route(new RoutingRequest(SensitivityClass.Restricted, purpose));

        decision.Fallbacks.Should().Equal(expectedFallback);
    }

    // T-30 ④, FR-10, ADR-0044 決定 1, IADR-0110: 計器の用途の値域は PurposeModels が閉じる。
    // 登録した 2 用途は名前のまま、未登録の用途は other へ（対照）。
    [Theory]
    [InlineData(Suggestion, Suggestion)]
    [InlineData(ClusterSummary, ClusterSummary)]
    [InlineData("graph-unregistered", LlmMetricValues.Other)]
    public void NormalizePurpose_KeepsRegisteredGraphPurposes(string purpose, string expected)
    {
        LlmMetricValues.NormalizePurpose(Deployed(), purpose).Should().Be(expected);
    }

    // T-30 ⑤, FR-10, ADR-0044 決定 1・3, ADR-0081 フォローアップ 2: **費用は 2 用途それぞれの軸に、他の用途と分けて積まれる。**
    // 陽性（2 用途の軸に トークン・金額 が載る）と陰性（`other` が 1 件も無い）を同じ購読で対にする。
    [Fact]
    public async Task PostComplete_GraphPurposes_AreMeteredUnderTheirOwnNames()
    {
        var client = factory.CreateClient();
        using var probe = new UsageProbe(factory.Services);

        foreach (var purpose in new[] { Suggestion, ClusterSummary })
        {
            (await client.PostAsJsonAsync("/complete",
                new { Prompt = "提案または要約", MaxTokens = 512, Confidentiality = "internal", Purpose = purpose },
                TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        }

        var suggestion = probe.Items.Where(m => m.Tags[LlmCompletionMetrics.PurposeTag] == Suggestion).ToList();
        suggestion.Should().Contain(m => m.Instrument == LlmUsageMetrics.TokensCounterName
            && m.Tags[LlmCompletionMetrics.ModelTag] == "claude-sonnet-5-5");
        suggestion.Should().Contain(m => m.Instrument == LlmUsageMetrics.CostCounterName && m.Value > 0,
            "claude-sonnet-5-5 は単価表にあるので金額へ換算される");

        var summary = probe.Items.Where(m => m.Tags[LlmCompletionMetrics.PurposeTag] == ClusterSummary).ToList();
        summary.Should().Contain(m => m.Instrument == LlmUsageMetrics.TokensCounterName
            && m.Tags[LlmCompletionMetrics.ModelTag] == "claude-opus-5-5");
        summary.Should().Contain(m => m.Instrument == LlmUsageMetrics.CostCounterName && m.Value > 0,
            "claude-opus-5-5 は単価表にあるので金額へ換算される");

        probe.Items.Should().NotContain(m => m.Tags[LlmCompletionMetrics.PurposeTag] == LlmMetricValues.Other,
            "未登録なら other へ集約され、提案生成・クラスタ要約の費用を切り分けられない（ADR-0081 フォローアップ 2）");
    }
}
