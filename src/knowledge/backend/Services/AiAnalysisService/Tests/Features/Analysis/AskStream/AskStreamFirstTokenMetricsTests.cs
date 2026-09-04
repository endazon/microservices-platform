using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using AiAnalysisService.Common.Observability;
using AiAnalysisService.Domain.Ports;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiAnalysisService.Tests.Features.Analysis.AskStream;

// NFR-02, NFR-21, FR-04, UC-01, SC-01, ADR-0006, ADR-0076 決定 5, IADR-0354 (#1204):
// /analysis/ask/stream の **初回トークンまでの時間（TTFT）** が計上されることを固定する。
//
// 計器が無いまま「応答完了 p95」を SLI の代理値として読んでいたのが #1204 の起点である。
// ADR-0076 は SLI を応答完了 p95 へ改める案を却下した（長い回答ほど SLO 違反になる逆向きの誘因）。
// **したがって「本文長に比例しない」ことを試験で示すこと自体が受け入れ基準である**（T-c）。
//
// 🔴 **MeterListener はプロセス全体の測定を購読する。** 他のテストクラスが並行して
// /analysis/ask/stream を叩くと測定が混入するため、**購読を Meter の「インスタンス」で絞る** ——
// IMeterFactory は容器ごとに別の Meter インスタンスを作るので、自分の factory の容器から
// 解決した RagStreamMetrics が使っている Meter と同一インスタンスのものだけを拾えば混入しない
// （クラスごとに factory が分かれる IClassFixture の性質を使う。Collection による直列化は要らない）。
[Trait("TestKind", "Integration")]
public class AskStreamFirstTokenMetricsTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private sealed record Measurement(double Value, Dictionary<string, string> Tags);

    // rag.answer.first_token.duration の測定を収集するリスナー（テストの寿命だけ購読する）。
    private sealed class FirstTokenProbe : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<Measurement> _measurements = [];

        public string? Unit { get; private set; }

        public FirstTokenProbe(IServiceProvider services)
        {
            // 🔴 **計器の生成を先に済ませる。** RagStreamMetrics は singleton であり、
            // 解決するまで Histogram が存在しない。存在しない計器は InstrumentPublished に載らず、
            // **購読しているつもりで何も見ていない**状態になる。
            _ = services.GetRequiredService<RagStreamMetrics>();
            var meter = services.GetRequiredService<IMeterFactory>()
                .Create(RagStreamMetrics.MeterName);

            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (ReferenceEquals(instrument.Meter, meter)
                        && instrument.Name == RagStreamMetrics.FirstTokenHistogramName)
                    {
                        Unit = instrument.Unit;
                        l.EnableMeasurementEvents(instrument);
                    }
                }
            };
            _listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
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

    private WebApplicationFactory<Program> With(IRagOrchestrator orchestrator) =>
        factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IRagOrchestrator>();
            s.AddSingleton(orchestrator);
        }));

    private static Task<HttpResponseMessage> AskAsync(HttpClient client) =>
        client.PostAsJsonAsync("/analysis/ask/stream",
            new { Question = "経費規程は？" }, TestContext.Current.CancellationToken);

    // T-a / T-d / T-e: 1 回呼ぶと 1 件記録され、単位は秒、属性は用途だけである。
    // **T-b（陰性）の陽性対照はこの試験である**（同じ probe の作り方で非ゼロが出ることを示す）。
    [Fact]
    public async Task PostAskStream_RecordsExactlyOneFirstTokenMeasurementInSeconds()
    {
        var app = With(new ScriptedRagOrchestrator(tokenCount: 2, delayBeforeLastToken: TimeSpan.Zero));
        using var probe = new FirstTokenProbe(app.Services);

        var resp = await AskAsync(app.CreateClient());
        await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        probe.Measurements.Should().HaveCount(1);
        var m = probe.Measurements[0];

        // 単位は**秒**である（ADR-0076 決定 1。ミリ秒だと 1000 倍ずれた閾値が静かに成立する）。
        probe.Unit.Should().Be("s");

        // 値は正であり、SLO（5 秒）よりはるかに小さい（スタブなので実質即時）。
        m.Value.Should().BeGreaterThan(0);
        m.Value.Should().BeLessThan(5);

        // 属性の値域が閉じている。用途 1 軸だけで、質問文・利用者識別子は載らない。
        m.Tags.Should().ContainKey(RagStreamMetrics.PurposeTag);
        m.Tags[RagStreamMetrics.PurposeTag].Should().Be(RagStreamMetrics.PurposeRagAnswer);
        m.Tags.Should().HaveCount(1);
        m.Tags.Values.Should().NotContain(v => v.Contains("経費規程", StringComparison.Ordinal));
    }

    // T-b: token が 1 件も出ずに error で終わったストリームでは記録しない（陰性）。
    // 0 を積むと「初回トークンが無かった」が「速かった」として p95 を下振れさせる。
    [Fact]
    public async Task PostAskStream_WhenNoTokenEmitted_RecordsNothing()
    {
        var app = With(new ErrorOnlyRagOrchestrator());
        using var probe = new FirstTokenProbe(app.Services);

        var resp = await AskAsync(app.CreateClient());
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // 🔴 陽性対照: 呼び出し自体は成立し、error イベントは確かに流れている。
        // 「0 件だった」が「そもそも経路が動いていない」ではないことを対で示す。
        body.Should().Contain("event: error");
        body.Should().NotContain("event: token");

        probe.Measurements.Should().BeEmpty();
    }

    // T-c: TTFT は本文長・応答完了時間に比例しない。
    // 最初の token の後に長い遅延を挟むと、**応答完了までの時間は伸びるが TTFT は伸びない**。
    // ADR-0076 が却下した「長い回答ほど SLO 違反になる」逆向きの誘因が入っていないことの確認である。
    [Fact]
    public async Task PostAskStream_FirstTokenDuration_DoesNotGrowWithResponseLength()
    {
        var tail = TimeSpan.FromMilliseconds(700);
        var app = With(new ScriptedRagOrchestrator(tokenCount: 11, delayBeforeLastToken: tail));
        using var probe = new FirstTokenProbe(app.Services);

        var wall = Stopwatch.StartNew();
        var resp = await AskAsync(app.CreateClient());
        await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        wall.Stop();

        // 応答完了までは遅延の分だけ確実に伸びている（比較の土台）。
        wall.Elapsed.Should().BeGreaterThan(tail);

        probe.Measurements.Should().HaveCount(1);
        // TTFT は遅延の前に確定しているため、応答完了時間の半分にも満たない。
        probe.Measurements[0].Value.Should().BeLessThan(tail.TotalSeconds / 2);
    }

    // citations → token*（最後の 1 件の直前に遅延）→ done を流すスタブ。
    private sealed class ScriptedRagOrchestrator(int tokenCount, TimeSpan delayBeforeLastToken)
        : StubOrchestratorBase
    {
        public override async IAsyncEnumerable<AskEvent> AskStreamAsync(string question, string userId,
            Dictionary<string, string> userAttributes,
            Dictionary<string, List<string>>? attributeFilters = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new AskCitationsEvent([Citation()]);
            for (var i = 0; i < tokenCount; i++)
            {
                if (i == tokenCount - 1 && delayBeforeLastToken > TimeSpan.Zero)
                    await Task.Delay(delayBeforeLastToken, ct);
                yield return new AskTokenEvent($"本文{i} [1]");
            }
            yield return new AskDoneEvent(Guid.NewGuid(), "claude-sonnet-4-6", 10, 20);
        }
    }

    // citations → error のみ（token を 1 件も出さない）。
    private sealed class ErrorOnlyRagOrchestrator : StubOrchestratorBase
    {
        public override async IAsyncEnumerable<AskEvent> AskStreamAsync(string question, string userId,
            Dictionary<string, string> userAttributes,
            Dictionary<string, List<string>>? attributeFilters = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new AskCitationsEvent([]);
            await Task.Yield();
            yield return new AskErrorEvent("生成に失敗しました。");
        }
    }

    // ストリーム以外の経路は本試験の対象外（呼ばれない）。
    private abstract class StubOrchestratorBase : IRagOrchestrator
    {
        protected static CitationDto Citation() =>
            new(1, Guid.NewGuid(), "文書A", Guid.NewGuid(), "s3://bucket/a.md", 0.9f, "抜粋");

        public Task<AiAnswerDto> AskAsync(string question, string userId,
            Dictionary<string, string> userAttributes,
            Dictionary<string, List<string>>? attributeFilters = null,
            CancellationToken ct = default)
            => Task.FromResult(new AiAnswerDto("テスト回答 [1]", [Citation()], "claude-sonnet-4-6", 10, 20));

        public Task<AiAnswerDto> AnalyzeAsync(AnalysisTaskRequest request, string userId,
            Dictionary<string, string> userAttributes, CancellationToken ct = default)
            => Task.FromResult(new AiAnswerDto("分析結果 [1]", [Citation()], "claude-sonnet-4-6", 10, 20));

        public abstract IAsyncEnumerable<AskEvent> AskStreamAsync(string question, string userId,
            Dictionary<string, string> userAttributes,
            Dictionary<string, List<string>>? attributeFilters = null,
            CancellationToken ct = default);
    }
}
