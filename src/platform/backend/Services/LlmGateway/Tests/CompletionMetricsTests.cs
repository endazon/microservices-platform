using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using AwesomeAssertions;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Ports;
using LlmGateway.Domain.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Shared.Contracts.Dtos;

namespace LlmGateway.Tests;

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

        await client.PostAsJsonAsync("/complete", Request("rag-answer"), TestContext.Current.CancellationToken);

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

        await client.PostAsJsonAsync("/complete", Request("rag-answer"), TestContext.Current.CancellationToken);

        probe.Measurements.Should().ContainSingle()
            .Which.Tags[LlmCompletionMetrics.StopReasonTag].Should().Be(CompletionStopReasons.MaxTokens);
    }

    // T-21c: 正常終了（分母に入るが分子には入らない）。
    [Fact]
    public async Task PostComplete_WhenEndTurn_CountsEndTurnStopReason()
    {
        using var probe = new MetricsProbe();
        var client = ClientReturning(new CompletionResult("回答本文", 11, 22, CompletionStopReasons.EndTurn));

        await client.PostAsJsonAsync("/complete", Request("rag-answer"), TestContext.Current.CancellationToken);

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

        await client.PostAsJsonAsync("/complete", Request("rag-answer"), TestContext.Current.CancellationToken);

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

        await client.PostAsJsonAsync("/complete", Request("experimental-purpose-xyz"), TestContext.Current.CancellationToken);

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

        await client.PostAsJsonAsync("/complete", Request("analysis", "confidential"), TestContext.Current.CancellationToken);

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

        await client.PostAsJsonAsync("/complete", Request("rag-answer"), TestContext.Current.CancellationToken);

        var m = probe.Measurements.Should().ContainSingle().Subject;
        m.Tags[LlmCompletionMetrics.ResultTag].Should().Be(LlmCompletionMetrics.ResultUpstreamError);
        m.Tags[LlmCompletionMetrics.StopReasonTag].Should().Be(LlmCompletionMetrics.ValueNone);
    }

    // T-25g, ADR-0038 決定 6 (#863), IADR-0225: **フォールバックの発火を可観測にする。**
    // 見送った第 1 候補は llm.result=fallback、成功した第 2 候補は sent として計上され、
    // llm.model が候補ごとに違う（＝用途別・モデル別の利用実績としてそのまま読める）。
    //
    // ★ 見送った呼び出しを upstream_error に混ぜないことが要点である。混ぜると
    //   「フォールバックで回復した呼び出し」が呼び出し先障害の率へ入り、
    //   upstream_error 率 > 10%（critical）のアラート方針が誤発火する。
    [Fact]
    public async Task PostComplete_WhenFallsBack_CountsFallbackThenSentWithDifferentModels()
    {
        using var probe = new MetricsProbe();
        var client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ILlmProvider>();
                // 第 1 候補（claude-opus-5）だけを HTTP 400 で失敗させる。
                var provider = new ModelFailingProvider("claude-opus-5", System.Net.HttpStatusCode.BadRequest);
                s.AddKeyedSingleton<ILlmProvider>("claude", provider);
                s.AddKeyedSingleton<ILlmProvider>("selfhosted", provider);
                s.AddKeyedSingleton<ILlmProvider>("copilot", provider);
            })).CreateClient();

        await client.PostAsJsonAsync("/complete", Request("analysis"), TestContext.Current.CancellationToken);

        probe.Measurements.Should().HaveCount(2, "見送った候補と成功した候補が別々に計上される");

        var fallback = probe.Measurements.Should()
            .ContainSingle(m => m.Tags[LlmCompletionMetrics.ResultTag] == LlmCompletionMetrics.ResultFallback)
            .Subject;
        fallback.Tags[LlmCompletionMetrics.ModelTag].Should().Be("claude-opus-5");
        fallback.Tags[LlmCompletionMetrics.PurposeTag].Should().Be("analysis");
        fallback.Tags[LlmCompletionMetrics.StopReasonTag].Should().Be(LlmCompletionMetrics.ValueNone);

        var sent = probe.Measurements.Should()
            .ContainSingle(m => m.Tags[LlmCompletionMetrics.ResultTag] == LlmCompletionMetrics.ResultSent)
            .Subject;
        sent.Tags[LlmCompletionMetrics.ModelTag].Should().Be("claude-sonnet-5");

        probe.Measurements.Should().NotContain(
            m => m.Tags[LlmCompletionMetrics.ResultTag] == LlmCompletionMetrics.ResultUpstreamError,
            "回復した呼び出しを呼び出し先障害として数えない");
    }

    // T-21h: ストリーミング経路（IADR-0037）も同じ属性で計上する（経路によって観測が欠けない）。
    [Fact]
    public async Task PostCompleteStream_WhenRefusal_CountsSentWithRefusalStopReason()
    {
        using var probe = new MetricsProbe();
        var client = ClientReturning(new CompletionResult("", 11, 0, CompletionStopReasons.Refusal));

        await client.PostAsJsonAsync("/complete/stream", Request("rag-answer"), TestContext.Current.CancellationToken);

        var m = probe.Measurements.Should().ContainSingle().Subject;
        m.Tags[LlmCompletionMetrics.ResultTag].Should().Be(LlmCompletionMetrics.ResultSent);
        m.Tags[LlmCompletionMetrics.StopReasonTag].Should().Be(CompletionStopReasons.Refusal);
    }

    // --- IADR-0212 (#786): 出力トークンの Histogram ------------------------------------

    // llm.completion.output_tokens の測定を収集する（Counter とは別の計器なので別プローブにする）。
    private sealed class OutputTokensProbe : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<Measurement> _measurements = [];

        public OutputTokensProbe()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == LlmCompletionMetrics.MeterName
                        && instrument.Name == LlmCompletionMetrics.OutputTokensHistogramName)
                        l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<int>((_, value, tags, _) =>
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

    // T-786a: 送信が成立したら出力トークン数を分布へ記録する（#380 の実測材料）。
    [Fact]
    public async Task PostComplete_WhenSent_RecordsOutputTokens()
    {
        using var probe = new OutputTokensProbe();
        var client = ClientReturning(new CompletionResult("回答本文", 11, 222, CompletionStopReasons.EndTurn));

        await client.PostAsJsonAsync("/complete", Request("rag-answer"), TestContext.Current.CancellationToken);

        var m = probe.Measurements.Should().ContainSingle().Subject;
        m.Value.Should().Be(222);
        m.Tags[LlmCompletionMetrics.StopReasonTag].Should().Be(CompletionStopReasons.EndTurn);
        m.Tags[LlmCompletionMetrics.PurposeTag].Should().Be("rag-answer");
        m.Tags[LlmCompletionMetrics.ModelTag].Should().NotBeNullOrWhiteSpace();
    }

    // T-786b: llm.result は Histogram の属性に載せない（常に sent で系列を分けないため。IADR-0212 決定 2）。
    [Fact]
    public async Task PostComplete_WhenSent_OmitsResultTagFromHistogram()
    {
        using var probe = new OutputTokensProbe();
        var client = ClientReturning(new CompletionResult("回答本文", 11, 22, CompletionStopReasons.EndTurn));

        await client.PostAsJsonAsync("/complete", Request("rag-answer"), TestContext.Current.CancellationToken);

        probe.Measurements.Should().ContainSingle()
            .Which.Tags.Should().NotContainKey(LlmCompletionMetrics.ResultTag);
    }

    // T-786c: ★ 送信していない経路は Histogram を記録しない（IADR-0212 決定 3）。
    //   0 を積むと分布の最下段が「短い応答」と「応答が無かった」の混合になり、上限到達の判断が濁る。
    //   Counter は逆に全経路で計上する（分母が欠けない）——この非対称が意図であることを固定する。
    [Fact]
    public async Task PostComplete_WhenEgressDenied_RecordsNoOutputTokensButStillCounts()
    {
        using var histogram = new OutputTokensProbe();
        using var counter = new MetricsProbe();
        // T-21d と同じ構成で越境が成立しない経路を通す（未承認のティア C しか無い状態）。
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

        await client.PostAsJsonAsync("/complete", Request("analysis", "confidential"), TestContext.Current.CancellationToken);

        counter.Measurements.Should().ContainSingle()
            .Which.Tags[LlmCompletionMetrics.ResultTag].Should().Be(LlmCompletionMetrics.ResultEgressDenied);
        histogram.Measurements.Should().BeEmpty();
    }

    // T-786d: upstream 例外も Histogram を記録しない（応答が返っていないのでトークン数が存在しない）。
    [Fact]
    public async Task PostComplete_WhenUpstreamError_RecordsNoOutputTokens()
    {
        using var probe = new OutputTokensProbe();
        var client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ILlmProvider>();
                var provider = new ThrowingProvider();
                s.AddKeyedSingleton<ILlmProvider>("claude", provider);
                s.AddKeyedSingleton<ILlmProvider>("selfhosted", provider);
                s.AddKeyedSingleton<ILlmProvider>("copilot", provider);
            })).CreateClient();

        await client.PostAsJsonAsync("/complete", Request("rag-answer"), TestContext.Current.CancellationToken);

        probe.Measurements.Should().BeEmpty();
    }

    // T-786e: ストリーミング経路も同じ値を記録する（経路によって観測が欠けない）。
    [Fact]
    public async Task PostCompleteStream_WhenSent_RecordsOutputTokens()
    {
        using var probe = new OutputTokensProbe();
        var client = ClientReturning(new CompletionResult("回答本文", 11, 77, CompletionStopReasons.EndTurn));

        await client.PostAsJsonAsync("/complete/stream", Request("rag-answer"), TestContext.Current.CancellationToken);

        probe.Measurements.Should().ContainSingle().Which.Value.Should().Be(77);
    }

    // T-786f: ストリームが Done を返さずに終わったら Histogram を記録しない（レビュー 🟢）。
    //   決定 3 は「未送信では 0 を埋めない」だが、**送信は成立したが最終チャンクを受け取れなかった**
    //   場合も 0 ではない。3 プロバイダは実際には Done を返すが、契約上は保証されていない。
    [Fact]
    public async Task PostCompleteStream_WhenNoDoneChunk_RecordsNoOutputTokens()
    {
        using var histogram = new OutputTokensProbe();
        using var counter = new MetricsProbe();
        var client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ILlmProvider>();
                var provider = new DonelessStreamProvider();
                s.AddKeyedSingleton<ILlmProvider>("claude", provider);
                s.AddKeyedSingleton<ILlmProvider>("selfhosted", provider);
                s.AddKeyedSingleton<ILlmProvider>("copilot", provider);
            })).CreateClient();

        await client.PostAsJsonAsync("/complete/stream", Request("rag-answer"), TestContext.Current.CancellationToken);

        // Counter は計上する（送信は成立している）が、分布には 0 を積まない。
        counter.Measurements.Should().ContainSingle()
            .Which.Tags[LlmCompletionMetrics.ResultTag].Should().Be(LlmCompletionMetrics.ResultSent);
        histogram.Measurements.Should().BeEmpty();
    }

    // Done: true のチャンクを一度も返さずに終わるストリーム（契約上あり得る形）。
    private sealed class DonelessStreamProvider : ILlmProvider
    {
        public Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
            => Task.FromResult(new CompletionResult("本文", 11, 22, CompletionStopReasons.EndTurn));

        public async IAsyncEnumerable<CompletionChunk> StreamAsync(
            CompletionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new CompletionChunk("本文");
            await Task.CompletedTask;
        }
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

    // #863: 指定モデルへの呼び出しだけを HTTP ステータス付きで失敗させる（フォールバックの発火用）。
    private sealed class ModelFailingProvider(string failingModel, System.Net.HttpStatusCode status) : ILlmProvider
    {
        public Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
            => string.Equals(request.Model, failingModel, StringComparison.Ordinal)
                ? throw new HttpRequestException($"upstream rejected {request.Model}", null, status)
                : Task.FromResult(new CompletionResult("本文", 11, 22, CompletionStopReasons.EndTurn));
    }
}
