using System.Diagnostics.Metrics;
using AwesomeAssertions;
using DocumentService.Domain.Ports;
using DocumentService.Features.Documents.ContentAbac;
using DocumentService.Tests.Infrastructure.ExternalServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocumentService.Tests.Features.Documents.ContentAbac;

// FR-05, FR-19, NFR-09, 計画 ADR-0121 決定 2・4, ADR-0119 決定 3, [[IADR-0481]] (#1665):
// **内容の ABAC の有効化の門**（issue のやること 3・4）。構成で有効にしても、所有者の読み取りのポリシーが無ければ開かない。
// 「無い・無効・形が違う」は認可サービス側で 0 件になる（`OwnerReadPolicyShapeTests` / `GrpcOwnerReadPolicyStatusTests`）ので、
// ここでは 0 件・数えられない・1 件以上の 3 通りと、構成の Off を見る。
[Trait("TestKind", "Unit")]
public class ContentAbacGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeSource(params int?[] answers) : IOwnerReadPolicyStatusSource
    {
        private int _next;
        public int Calls { get; private set; }

        public Task<int?> GetActiveCountAsync(CancellationToken ct)
        {
            Calls++;
            var answer = answers[Math.Min(_next, answers.Length - 1)];
            _next++;
            return Task.FromResult(answer);
        }
    }

    // ゲージは自分の IMeterFactory の器だけを聞く。
    private static (IReadOnlyList<(int Value, string? State)> Read, MeterListener Listener) GaugeProbe(
        IMeterFactory factory, out Func<IReadOnlyList<(int Value, string? State)>> collect)
    {
        var seen = new List<(int, string?)>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, factory) && instrument.Name == ContentAbacGate.OpenGaugeName)
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<int>((_, value, tags, _) =>
        {
            string? state = null;
            foreach (var tag in tags)
                if (tag.Key == ContentAbacGate.StateTag) state = (string?)tag.Value;
            seen.Add((value, state));
        });
        listener.Start();
        collect = () =>
        {
            seen.Clear();
            listener.RecordObservableInstruments();
            return [.. seen];
        };
        return (seen, listener);
    }

    private static (ContentAbacGate Gate, RecordingLogger<ContentAbacGate> Log, IMeterFactory Meters) NewGate(ContentAbacMode mode)
    {
        var meters = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
        var log = new RecordingLogger<ContentAbacGate>();
        return (new ContentAbacGate(new ContentAbacOptions(mode), meters, log), log, meters);
    }

    // T-48: 構成が Off（既定）なら閉じたまま、認可サービスを呼ばない。
    [Fact]
    public async Task 構成がOffなら閉じたまま認可サービスを呼ばない()
    {
        var (gate, _, meters) = NewGate(ContentAbacMode.Off);
        using var listener = GaugeProbe(meters, out var collect).Listener;
        var source = new FakeSource(5);

        (await gate.EvaluateAsync(source, Ct)).Should().Be(ContentAbacGateState.Disabled);

        gate.IsOpen.Should().BeFalse();
        source.Calls.Should().Be(0);
        collect().Should().Equal((0, "disabled"));
    }

    // T-49: 陽性対照。On で 1 件以上を確かめたら開く（計器は 1・open）。
    [Fact]
    public async Task 構成がOnでポリシーが在れば開く()
    {
        var (gate, log, meters) = NewGate(ContentAbacMode.On);
        using var listener = GaugeProbe(meters, out var collect).Listener;
        collect().Should().Equal((0, "not_evaluated"));

        (await gate.EvaluateAsync(new FakeSource(1), Ct)).Should().Be(ContentAbacGateState.Open);

        gate.IsOpen.Should().BeTrue();
        collect().Should().Equal((1, "open"));
        log.OfLevel(LogLevel.Information).Should().ContainSingle();
    }

    // T-50: 否定の試験。On でも 0 件なら開かない。閉じた理由をログ（Warning）と計器（0・state）に残す。
    [Fact]
    public async Task 構成がOnでもポリシーが無ければ閉じたまま理由を残す()
    {
        var (gate, log, meters) = NewGate(ContentAbacMode.On);
        using var listener = GaugeProbe(meters, out var collect).Listener;

        (await gate.EvaluateAsync(new FakeSource(0), Ct)).Should().Be(ContentAbacGateState.OwnerReadPolicyAbsent);

        gate.IsOpen.Should().BeFalse();
        collect().Should().Equal((0, "owner_read_policy_absent"));
        log.OfLevel(LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().Contain("所有者の読み取りのポリシーが有効な状態で 1 件も無い");
    }

    // T-50: 数えられない（認可サービスに届かない・未構成）も開かない（fail-closed）。
    [Fact]
    public async Task 数えられなければ閉じたまま理由を残す()
    {
        var (gate, log, meters) = NewGate(ContentAbacMode.On);
        using var listener = GaugeProbe(meters, out var collect).Listener;

        (await gate.EvaluateAsync(new FakeSource([null]), Ct)).Should().Be(ContentAbacGateState.OwnerReadPolicyUnknown);

        gate.IsOpen.Should().BeFalse();
        collect().Should().Equal((0, "owner_read_policy_unknown"));
        log.OfLevel(LogLevel.Warning).Should().ContainSingle().Which.Message.Should().Contain("数えられない");
    }

    // T-51: 無い → 投入 → 次の評価で開く。1 度開いたら、後で消えても閉じない（消失は認可サービスの警報が知らせる）。
    [Fact]
    public async Task 投入されたら開き一度開いたら閉じない()
    {
        var (gate, _, _) = NewGate(ContentAbacMode.On);
        var source = new FakeSource(0, 1, 0);

        (await gate.EvaluateAsync(source, Ct)).Should().Be(ContentAbacGateState.OwnerReadPolicyAbsent);
        (await gate.EvaluateAsync(source, Ct)).Should().Be(ContentAbacGateState.Open);
        (await gate.EvaluateAsync(source, Ct)).Should().Be(ContentAbacGateState.Open);

        gate.IsOpen.Should().BeTrue();
        source.Calls.Should().Be(2, "開いた後は問い合わせない");
    }

    // 答えを外から放つ源（並行の評価の順序を決めるため）。
    private sealed class PendingSource : IOwnerReadPolicyStatusSource
    {
        private readonly TaskCompletionSource<int?> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<int?> GetActiveCountAsync(CancellationToken ct) => _answer.Task;
        public void Answer(int? count) => _answer.SetResult(count);
    }

    // T-68（#1676）: **並行の評価でもラッチは閉じ直さない。** 閉じた門で 2 つの評価が同時に問い合わせ、
    // 先に「在る」が返って開いた後に、遅れて「無い」が返っても開いたままである（遅れた評価も「開」を返す）。
    // 今の呼び出し元は常駐の 1 本だけだが、評価を別の経路から呼ぶようになると、ロック内の再確認が無ければ閉じ直す
    // （許可が広がる向きではないが、1 度開いた門が閉じると読み取りの判定が要求ごとに揺れる）。
    [Fact]
    public async Task 並行の評価でも一度開いた門を閉じ直さない()
    {
        var (gate, _, _) = NewGate(ContentAbacMode.On);
        var present = new PendingSource();
        var absent = new PendingSource();

        var first = gate.EvaluateAsync(present, Ct);
        var late = gate.EvaluateAsync(absent, Ct);
        first.IsCompleted.Should().BeFalse("両方の評価が問い合わせの途中にいる");
        late.IsCompleted.Should().BeFalse();

        present.Answer(1);
        (await first).Should().Be(ContentAbacGateState.Open);
        gate.IsOpen.Should().BeTrue("陽性対照: 先の評価で開いた");

        absent.Answer(0);
        (await late).Should().Be(ContentAbacGateState.Open, "遅れて返った「無い」で閉じ直さない");
        gate.IsOpen.Should().BeTrue();
        gate.State.Should().Be(ContentAbacGateState.Open);
    }

    [Theory]
    [InlineData(null, ContentAbacMode.Off)]
    [InlineData("", ContentAbacMode.Off)]
    [InlineData("off", ContentAbacMode.Off)]
    [InlineData("On", ContentAbacMode.On)]
    public void 構成を読む(string? declared, ContentAbacMode expected)
        => ContentAbacOptions.FromConfiguration(Config(declared)).Mode.Should().Be(expected);

    // 値域外は起動時に落とす（打ち間違いを Off へも On へも黙って倒さない）。
    [Theory]
    [InlineData("true")]
    [InlineData("enabled")]
    [InlineData("1")]
    public void 構成の値域外は起動時に落ちる(string declared)
    {
        var act = () => ContentAbacOptions.FromConfiguration(Config(declared));
        act.Should().Throw<InvalidOperationException>().WithMessage($"*{ContentAbacOptions.ModeKey}*");
    }

    private static IConfiguration Config(string? declared)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ContentAbacOptions.ModeKey] = declared })
            .Build();

    // 常駐: On なら起動時に評価し、開いたら止まる。Off なら評価しない。
    [Theory(Timeout = 30_000)]
    [InlineData(ContentAbacMode.On, true)]
    [InlineData(ContentAbacMode.Off, false)]
    public async Task 常駐は構成がOnのときだけ起動時に評価する(ContentAbacMode mode, bool expectOpen)
    {
        var (gate, _, _) = NewGate(mode);
        var source = new FakeSource(1);
        var services = new ServiceCollection();
        services.AddSingleton<IOwnerReadPolicyStatusSource>(source);
        using var provider = services.BuildServiceProvider();
        using var hosted = new ContentAbacGateHostedService(
            gate, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ContentAbacGateHostedService>.Instance);

        await hosted.StartAsync(Ct);
        for (var i = 0; i < 500 && expectOpen && !gate.IsOpen; i++) await Task.Delay(20, Ct);
        await hosted.StopAsync(Ct);

        gate.IsOpen.Should().Be(expectOpen);
        source.Calls.Should().Be(expectOpen ? 1 : 0);
    }
}
