using System.Diagnostics.Metrics;
using System.Net;
using AwesomeAssertions;
using GraphService.Common.Observability;
using GraphService.Domain.Ports;
using GraphService.Infrastructure.ExternalServices;
using GraphService.Tests.Grpc;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pb = Knowledge.Contracts.Grpc.Dashboard.V1;

namespace GraphService.Tests.Infrastructure.ExternalServices;

// FR-10, FR-17, FR-19, NFR-09, NFR-16, NFR-21, UC-05, SC-10, ADR-0006, ADR-0029, ADR-0075, ADR-0076,
// [[IADR-0256]] 決定 3, [[IADR-0265]], [[IADR-0299]], [[IADR-0353]], [[IADR-0379]], [[IADR-0389]] 決定 5,
// [[IADR-0408]] (#1255): 観測値の報告の gRPC 実装が、**旧 REST 実装と同じ枝・同じ副作用**であることを固定する（REST 実装は [[IADR-0533]] で撤去した）。
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
    // #1637 で本物のチャネルの形へ改めた: 127.0.0.1 の実サーバーで受け口が要求を受け取ってから呼び出し元が取り消す。
    // 本物のチャネルはこれを `RpcException(Cancelled)` で投げるので、素の OCE を注入する形では `catch (RpcException)` が
    // 停止を「報告の失敗」（Error のログ）へ畳んでも緑になっていた。周期のループ（`KnowledgeHealthHostedService`）は
    // 停止要求の OCE だけを静かに終わる合図として読むので、**外へ出る型が OCE であること**まで測る。
    [Fact]
    public async Task 呼び出し元のキャンセルは伝播する()
    {
        var (metrics, probe) = NewProbe();
        using var _ = probe;
        var service = new ReportService(ServerBehavior.Hang);
        await using var server = await LoopbackGrpcServer.StartAsync(service, Ct);
        var logger = new RecordingLogger<GrpcKnowledgeHealthReporter>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var reporter = new GrpcKnowledgeHealthReporter(
            new Pb.KnowledgeHealthReport.KnowledgeHealthReportClient(server.Channel), metrics,
            TimeProvider.System, logger);

        var call = reporter.ReportAsync(Indicator, [], ct: cts.Token);
        await service.Received.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cts.CancelAsync();

        var thrown = await FluentActions.Awaiting(() => call).Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.CancellationToken.Should().Be(cts.Token, "呼び出し元の取り消しとして外へ出す");
        probe.Measurements.Should().BeEmpty();
        logger.OfLevel(LogLevel.Error).Should().BeEmpty("停止は報告の失敗ではない");
    }

    // 🔴 T-09b: **呼び出し元が取り消していない `CANCELLED` は従来どおり「受理されない」枝である**
    // （数えず・投げず・Error。status だけで判定する変異を落とす対照）。
    [Fact]
    public async Task 受け口が返した_Cancelled_は数えず投げない()
    {
        var (metrics, probe) = NewProbe();
        using var _ = probe;
        var service = new ReportService(ServerBehavior.ReturnCancelled);
        await using var server = await LoopbackGrpcServer.StartAsync(service, Ct);
        var logger = new RecordingLogger<GrpcKnowledgeHealthReporter>();
        var reporter = new GrpcKnowledgeHealthReporter(
            new Pb.KnowledgeHealthReport.KnowledgeHealthReportClient(server.Channel), metrics,
            TimeProvider.System, logger);

        var act = async () => await reporter.ReportAsync(Indicator, [], ct: Ct);

        await act.Should().NotThrowAsync();
        probe.Measurements.Should().BeEmpty();
        logger.OfLevel(LogLevel.Error).Should().ContainSingle("★ 陽性対照 —— 縮退の枝は Error を出す");
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

    // 🔴 送出 1 回あたりの期限は `GrpcKnowledgeHealthReporter.SendTimeout`（5 秒。［2026-10-10 / #1255］[[IADR-0533]] で
    // 撤去した REST 実装の `HttpClient.Timeout` から移した）。呼び出しごとの deadline で付ける。
    [Fact]
    public async Task 送出の期限は構成の上限である()
    {
        var (metrics, probe) = NewProbe();
        using var _ = probe;
        var now = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock(now);
        var fake = new FakeClient(new Pb.ReportResponse());

        await Reporter(fake, metrics, clock).ReportAsync(
            Indicator, [], ct: TestContext.Current.CancellationToken);

        fake.LastOptions.Deadline.Should().Be(
            now.UtcDateTime.Add(GrpcKnowledgeHealthReporter.SendTimeout));
    }

    // 🔴 T-11: **宛先の有無で登録の形が変わる。** `Services:DashboardServiceGrpc` が無ければ
    // 生成クライアントは UNAVAILABLE を返す呼び出し器の上に組まれる（［2026-10-10 / #1255］[[IADR-0533]] 決定 2。
    // 従前は 1 つも登録せず、`Program.cs` が REST 実装と gRPC 実装を選んでいた。REST 実装は撤去した）。
    //
    // 🔴 **`Program.cs` の DI をテストホストの構成で切り替えて測ることはできない。**
    // 選択は組み立て時（`builder.Configuration[...]`）に行われ、`WebApplicationFactory` が
    // 差し込む構成は Build 時に載るためである（`SimilaritySourceWiringTests` の注記と同じ罠）。
    // したがって**登録関数そのもの**を陽性・陰性の対で固定する。
    [Fact]
    public void 宛先が未設定でも届かない宛先として登録する()
    {
        // ［2026-10-10 / #1255］[[IADR-0533]] 決定 2: 未設定でも生成クライアントは登録され、呼び出しは UNAVAILABLE で失敗する
        // （従前は何も登録せず、`Program.cs` が REST 実装へ倒していた。REST 実装は撤去した）。
        var services = new ServiceCollection()
            .AddKnowledgeHealthGrpcClient(new ConfigurationBuilder().Build());

        services.Should().ContainSingle(d => d.ServiceType == typeof(Pb.KnowledgeHealthReport.KnowledgeHealthReportClient));
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private enum ServerBehavior { Hang, ReturnCancelled }

    // 実サーバーに載せる受け口の偽物（`GrpcDocumentTagWriterTests.TagWriteService` と同型）。
    private sealed class ReportService(ServerBehavior behavior) : Pb.KnowledgeHealthReport.KnowledgeHealthReportBase
    {
        public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<Pb.ReportResponse> Report(Pb.ReportRequest request, ServerCallContext context)
        {
            Received.TrySetResult();
            if (behavior == ServerBehavior.ReturnCancelled)
                throw new RpcException(new Status(StatusCode.Cancelled, "受け口が取り消した"));
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
            return new Pb.ReportResponse();
        }
    }

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
    // この試験が要るのは「固定の現在時刻」だけなので、自前の小さな派生で足りる（本プロジェクトは #1622 から
    // `Microsoft.Extensions.TimeProvider.Testing` の `FakeTimeProvider` も参照しており、使ってもよい）。
    // `scripts/backend-library-baseline.json` の ratchet が縛るのは不採用ライブラリ（ADR-0030）だけで、この選択とは関係しない（#1630）。
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
