using System.Diagnostics.Metrics;
using System.Net;
using AwesomeAssertions;
using GraphService.Common.Observability;
using GraphService.Domain.Ports;
using GraphService.Infrastructure.ExternalServices;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pb = Knowledge.Contracts.Grpc.Dashboard.V1;

namespace GraphService.Tests.Infrastructure.ExternalServices;

// FR-10, FR-17, FR-19, NFR-09, NFR-16, NFR-21, UC-05, SC-10, ADR-0006, ADR-0029, ADR-0075, ADR-0076,
// [[IADR-0256]] 決定 3, [[IADR-0265]], [[IADR-0299]], [[IADR-0353]], [[IADR-0379]], [[IADR-0389]] 決定 5,
// [[IADR-0407]] (#1255): 観測値の報告の gRPC 実装が、**REST 実装と同じ枝・同じ副作用**であることを固定する。
//
// 🔴 ここが本スライスの不変条件そのものである —— 輸送を替えたときに
// **「届いていないのに届いたことになる」**か**「届かないと業務処理が止まる」**の
// どちらかへ倒れると、指標の沈黙が鳴らなくなる（前者）か、購読が止まる（後者）。
// 陽性（成功時に 1 件数える）と陰性（失敗時は数えない）を対で置く。
[Trait("TestKind", "Unit")]
public class GrpcKnowledgeHealthReporterTests
{
    private const string Indicator = "unresolved-links";

    // 🔴 T-01 陽性対照。**受理されたときだけ数える。**
    // これが無いと、以下の陰性はすべて「何も数えない」実装でも緑になる。
    [Fact]
    public async Task 受理されたら指標名つきで1件数える()
    {
        var (metrics, probe) = NewProbe();
        using var _ = probe;
        var reporter = Reporter(new FakeClient(new Pb.ReportResponse { Indicator = Indicator, Accepted = 0 }), metrics);

        await reporter.ReportAsync(Indicator, [], ct: TestContext.Current.CancellationToken);

        probe.Measurements.Should().ContainSingle();
        probe.Measurements[0].Value.Should().Be(1);
        probe.Measurements[0].Indicator.Should().Be(Indicator);
    }

    // 🔴 T-07 陰性。**受け口が受理しなかったら数えない・投げない。**
    // ここで数えると、受け口が壊れている間も系列が生き続けて `absent` が沈黙する
    // （[[IADR-0256]] 決定 3・[[IADR-0389]] 決定 5。REST 実装の「非 2xx でも数えない」と同値）。
    [Theory]
    [InlineData(StatusCode.InvalidArgument)]
    [InlineData(StatusCode.Internal)]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.DeadlineExceeded)]
    public async Task RpcException_は数えず投げない(StatusCode status)
    {
        var (metrics, probe) = NewProbe();
        using var _ = probe;
        var reporter = Reporter(
            new ThrowingClient(new RpcException(new Status(status, "受理されない"))), metrics);

        var act = async () => await reporter.ReportAsync(
            Indicator, [], ct: TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync("送出の失敗で購読ホストを止めない（fail-open）");
        probe.Measurements.Should().BeEmpty("届いていない事実は残す");
    }

    // 🔴 T-08: s2s トークンの取得失敗（`ClientCredentialsServiceTokenProvider` の
    // `InvalidOperationException`）も**同じ縮退**である —— 構成不備で報告が止まることはあっても、
    // 定期処理そのものは落とさない（REST 実装の `catch (Exception ex) when (...)` と同値）。
    [Fact]
    public async Task s2s_トークン取得失敗も数えず投げない()
    {
        var (metrics, probe) = NewProbe();
        using var _ = probe;
        var reporter = Reporter(
            new ThrowingClient(new InvalidOperationException("ServiceToken:ClientId が未設定です。")), metrics);

        var act = async () => await reporter.ReportAsync(
            Indicator, [], ct: TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        probe.Measurements.Should().BeEmpty();
    }

    // 🔴 T-09: **呼び出し元のキャンセルだけは伝播する。** 握ると「キャンセルされたのに続行した」
    // ように見える（REST 実装の `IsCallerCancellation` と同値）。
    [Fact]
    public async Task 呼び出し元のキャンセルは伝播する()
    {
        var (metrics, probe) = NewProbe();
        using var _ = probe;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var reporter = Reporter(
            new ThrowingClient(new OperationCanceledException(cts.Token)), metrics);

        var act = async () => await reporter.ReportAsync(Indicator, [], ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        probe.Measurements.Should().BeEmpty();
    }

    // 🔴 T-02/T-03 の送信側: **null を presence にしない。** `DocScope` / `Dimension` の null を
    // 空文字で送ると、受け口では「個人資料ではない」と区別できず、内訳に `""` の軸が生まれる。
    // `ThresholdDays` の null を 0 で送ると、受け口の検証器が 400 を返し
    // **しきい値を持たない 3 指標の報告が全部落ちる**。
    [Fact]
    public async Task 未設定の項目は_presence_を立てない()
    {
        var (metrics, probe) = NewProbe();
        using var _ = probe;
        var fake = new FakeClient(new Pb.ReportResponse());
        var reporter = Reporter(fake, metrics);

        await reporter.ReportAsync(Indicator, [
            new KnowledgeHealthObservation("軸なし", null),
            new KnowledgeHealthObservation("個人資料", "private-note", "not-found"),
        ], ct: TestContext.Current.CancellationToken);

        var sent = fake.LastRequest!;
        sent.HasThresholdDays.Should().BeFalse("しきい値を持たない指標では項目そのものを出さない");

        var plain = sent.Observations.Single(o => o.SubjectKey == "軸なし");
        plain.HasDocScope.Should().BeFalse("null は空文字ではない");
        plain.HasDimension.Should().BeFalse("null は空文字ではない");

        // 陽性対照 —— 持つ側は運ばれる（「立てない」のは presence を捨てているからではない）。
        var scoped = sent.Observations.Single(o => o.SubjectKey == "個人資料");
        scoped.DocScope.Should().Be("private-note");
        scoped.Dimension.Should().Be("not-found");
    }

    // 陽性対照（しきい値側）: 持つ指標では presence が立ち、値が運ばれる。
    [Fact]
    public async Task しきい値を持つ指標では_presence_が立つ()
    {
        var (metrics, probe) = NewProbe();
        using var _ = probe;
        var fake = new FakeClient(new Pb.ReportResponse());

        await Reporter(fake, metrics).ReportAsync(
            "stale-documents", [], 180, TestContext.Current.CancellationToken);

        fake.LastRequest!.HasThresholdDays.Should().BeTrue();
        fake.LastRequest.ThresholdDays.Should().Be(180);
    }

    // 🔴 タイムアウトは REST 側と**同じ 5 秒**である（`HttpClient.Timeout` の代わりに deadline）。
    // 値は `HttpKnowledgeHealthReporter.SendTimeout` を**そのまま引く** ——
    // 書き写すと片方だけ動いたときに気付けない。
    [Fact]
    public async Task 送出の期限は_REST_と同じ値である()
    {
        var (metrics, probe) = NewProbe();
        using var _ = probe;
        var now = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock(now);
        var fake = new FakeClient(new Pb.ReportResponse());

        await Reporter(fake, metrics, clock).ReportAsync(
            Indicator, [], ct: TestContext.Current.CancellationToken);

        fake.LastOptions.Deadline.Should().Be(
            now.UtcDateTime.Add(HttpKnowledgeHealthReporter.SendTimeout));
    }

    // 🔴 T-11: **切替は構成の有無だけである。** `Services:DashboardServiceGrpc` が無ければ
    // 生成クライアントを**1 つも登録しない** —— 登録の有無で `Program.cs` が REST 実装と
    // gRPC 実装を選ぶ（並走中の正は REST。戻すのは構成を外すだけでコードは変えない）。
    //
    // 🔴 **`Program.cs` の DI をテストホストの構成で切り替えて測ることはできない。**
    // 選択は組み立て時（`builder.Configuration[...]`）に行われ、`WebApplicationFactory` が
    // 差し込む構成は Build 時に載るためである（`SimilaritySourceWiringTests` の注記と同じ罠）。
    // したがって**登録関数そのもの**を陽性・陰性の対で固定する。
    [Fact]
    public void 宛先が未設定なら生成クライアントを登録しない()
    {
        var services = new ServiceCollection()
            .AddKnowledgeHealthGrpcClient(new ConfigurationBuilder().Build());

        services.Should().NotContain(
            d => d.ServiceType == typeof(Pb.KnowledgeHealthReport.KnowledgeHealthReportClient),
            "未設定なら何も登録しない（REST のまま）");
    }

    [Fact]
    public void 宛先が構成されていれば生成クライアントを登録する()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KnowledgeHealthGrpcClientExtensions.AddressKey] = "http://dashboard-service:8081",
        }).Build();

        var services = new ServiceCollection().AddKnowledgeHealthGrpcClient(config);

        services.Should().Contain(
            d => d.ServiceType == typeof(Pb.KnowledgeHealthReport.KnowledgeHealthReportClient),
            "★ 陽性対照 —— 登録されないのは関数が壊れているからではない");
    }

    // ── 器 ────────────────────────────────────────────────────────

    private static GrpcKnowledgeHealthReporter Reporter(
        Pb.KnowledgeHealthReport.KnowledgeHealthReportClient client,
        KnowledgeHealthReportMetrics metrics,
        TimeProvider? clock = null) =>
        new(client, metrics, clock ?? TimeProvider.System,
            NullLogger<GrpcKnowledgeHealthReporter>.Instance);

    private static (KnowledgeHealthReportMetrics Metrics, CounterProbe Probe) NewProbe()
    {
        var factory = new ServiceCollection().AddMetrics().BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();
        var metrics = new KnowledgeHealthReportMetrics(factory);
        return (metrics, new CounterProbe(factory.Create(KnowledgeHealthReportMetrics.MeterName)));
    }

    // 実行時の計測を拾う。**Meter の「インスタンス」と計器名で絞る**（[[IADR-0394]] / #1275）——
    // `microservices-platform.graph-service` は production の定数であり、同じ名前の Meter を
    // 別容器から作る試験クラスが並行して同じ計器を発行する（`BeEmpty()` が非決定的に破れる）。
    private sealed class CounterProbe : IDisposable
    {
        public List<(long Value, string? Indicator)> Measurements { get; } = [];
        private readonly MeterListener _listener;

        public CounterProbe(Meter meter)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (ReferenceEquals(instrument.Meter, meter)
                        && instrument.Name == KnowledgeHealthReportMetrics.ReportCounterName)
                        listener.EnableMeasurementEvents(instrument);
                },
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                string? indicator = null;
                foreach (var tag in tags)
                    if (tag.Key == KnowledgeHealthReportMetrics.IndicatorTag)
                        indicator = tag.Value?.ToString();
                Measurements.Add((value, indicator));
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    // 既存の試験（`KnowledgeHealthProducerTests` ほか）と同じ形の固定時計。
    // **新しいライブラリ（Microsoft.Extensions.TimeProvider.Testing）は足さない**
    // —— `scripts/backend-library-baseline.json` の ratchet に触れる。
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeClient(Pb.ReportResponse response)
        : Pb.KnowledgeHealthReport.KnowledgeHealthReportClient
    {
        public Pb.ReportRequest? LastRequest { get; private set; }
        public CallOptions LastOptions { get; private set; }

        public override AsyncUnaryCall<Pb.ReportResponse> ReportAsync(
            Pb.ReportRequest request, CallOptions options)
        {
            LastRequest = request;
            LastOptions = options;
            return new AsyncUnaryCall<Pb.ReportResponse>(
                Task.FromResult(response), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
        }
    }

    private sealed class ThrowingClient(Exception exception)
        : Pb.KnowledgeHealthReport.KnowledgeHealthReportClient
    {
        public override AsyncUnaryCall<Pb.ReportResponse> ReportAsync(
            Pb.ReportRequest request, CallOptions options) =>
            new(Task.FromException<Pb.ReportResponse>(exception), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
    }
}
