using System.Diagnostics.Metrics;
using AwesomeAssertions;
using DocumentService.Common.Observability;
using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.ExternalServices;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pb = Platform.Shared.Contracts.Grpc.Notification.V1;

namespace DocumentService.Tests.Infrastructure.ExternalServices;

// FR-19, FR-20, FR-21, FR-22, NFR-09, NFR-16, NFR-19, ADR-0029, ADR-0037 決定 6・17・18,
// ADR-0045 決定 8, ADR-0075, [[IADR-0215]] 決定 3・5-b, [[IADR-0270]] 決定 6, [[IADR-0379]],
// [[IADR-0408]], [[IADR-0412]] 決定 5, [[IADR-0417]] 決定 9, [[IADR-0419]] (#1255):
// 通知の送出の gRPC 実装が、**REST 実装と同じ枝・同じ副作用**であることを固定する。
//
// 🔴 ここが本スライスの不変条件そのものである —— 輸送を替えたときに
// **「届かないと業務処理が止まる」**（fail-open が壊れる）か
// **「届かなかったことが数えられない」**（静かに落ちる）のどちらかへ倒れると、
// 個人資料の 3 契機の通知が誰にも気づかれないまま消える。
[Trait("TestKind", "Unit")]
public class GrpcPrivateNoteNotifierTests
{
    private const string Kind = PrivateNoteNotificationKinds.PrivateNotePurgeWeekly;

    private static readonly DateTimeOffset Occurred = new(2026, 9, 9, 9, 0, 0, TimeSpan.Zero);

    // ── 1. 結末が計器に載る（陽性・陰性の対） ────────────────────────────────

    // 🔴 T-01 陽性対照。**受理されたら `sent` を 1 件数える。**
    // これが無いと、以下の陰性はすべて「何も数えない」実装でも緑になる。
    [Fact]
    public async Task 受理されたら種別つきで_sent_を1件数える()
    {
        var (metrics, probe) = NewProbe();
        using var _ = probe;
        var notifier = Notifier(new FakeClient(new Pb.AcceptResponse()), metrics);

        await notifier.NotifyAsync("owner-a", Kind, Occurred, count: 2,
            ct: TestContext.Current.CancellationToken);

        probe.Measurements.Should().ContainSingle();
        probe.Measurements[0].Value.Should().Be(1);
        probe.Measurements[0].Outcome.Should().Be(PrivateNoteNotificationMetrics.OutcomeSent);
        probe.Measurements[0].Kind.Should().Be(Kind);
    }

    // 🔴 T-02: **受け口へ届いたが拒まれた**（REST の非 2xx と同値）→ `rejected`。
    // 🔴 `UNAUTHENTICATED` / `PERMISSION_DENIED` もここである —— **要求は届いており、拒んだのは
    // 受け口である**（realm の service account の配線漏れがこの枝に出る。#1301 と同型の事故）。
    [Theory]
    [InlineData(StatusCode.InvalidArgument)]
    [InlineData(StatusCode.Internal)]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.PermissionDenied)]
    public async Task 受け口が答えた失敗は_rejected_である(StatusCode status)
    {
        var (metrics, probe) = NewProbe();
        using var _ = probe;
        var notifier = Notifier(
            new ThrowingClient(new RpcException(new Status(status, "受理されない"))), metrics);

        var act = async () => await notifier.NotifyAsync("owner-a", Kind, Occurred,
            ct: TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync("通知は本体操作の従属物ではない（fail-open）");
        probe.Measurements.Should().ContainSingle()
            .Which.Outcome.Should().Be(PrivateNoteNotificationMetrics.OutcomeRejected);
    }

    // 🔴 T-03: **後段へ 1 バイトも届いていない** → `unreachable`（REST の例外枝と同値）。
    // 🔴 T-02 と対で置く。**全 status を「不達」へ畳むと `rejected` の枝が静かに消え**、
    // ペイロード・配備の不整合が「届かなかった」に見える（打つ手が違う）。
    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.DeadlineExceeded)]
    public async Task 後段へ届かなかったものは_unreachable_である(StatusCode status)
    {
        var (metrics, probe) = NewProbe();
        using var _ = probe;
        var notifier = Notifier(
            new ThrowingClient(new RpcException(new Status(status, "届かない"))), metrics);

        var act = async () => await notifier.NotifyAsync("owner-a", Kind, Occurred,
            ct: TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        probe.Measurements.Should().ContainSingle()
            .Which.Outcome.Should().Be(PrivateNoteNotificationMetrics.OutcomeUnreachable);
    }

    // 🔴 T-04: **s2s トークンの取得失敗も `unreachable`。** `RpcException` にならないので
    // 型の列挙から漏れる —— [[IADR-0215]] 決定 5-b（#600）が REST 側で踏んだ穴と同型である。
    [Fact]
    public async Task s2s_トークン取得失敗は_unreachable_で投げない()
    {
        var (metrics, probe) = NewProbe();
        using var _ = probe;
        var notifier = Notifier(
            new ThrowingClient(new InvalidOperationException("client secret が無い")), metrics);

        var act = async () => await notifier.NotifyAsync("owner-a", Kind, Occurred,
            ct: TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        probe.Measurements.Should().ContainSingle()
            .Which.Outcome.Should().Be(PrivateNoteNotificationMetrics.OutcomeUnreachable);
    }

    // 🔴 T-05: **呼び出し元のキャンセルだけは伝播させる**（REST 版と同じ）。
    // 握ると「キャンセルされたのに続行した」ように見える。
    [Fact]
    public async Task 呼び出し元のキャンセルは伝播する()
    {
        var (metrics, probe) = NewProbe();
        using var _ = probe;
        var notifier = Notifier(new ThrowingClient(new OperationCanceledException()), metrics);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await notifier.NotifyAsync("owner-a", Kind, Occurred, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        probe.Measurements.Should().BeEmpty("キャンセルは通知の失敗ではない");
    }

    // ── 2. 面へ写す形（presence と自由文の不在） ──────────────────────────────

    // 🔴 T-06: **未設定の項目は presence を立てない。** 代入すると proto3 の既定（`0`）が
    // 「値」として届き、**検証は通る**ので例外は 1 つも起きず、受け口の重複判定だけが静かに割れる
    // （同じ事象が新規として二重に積まれる）。
    [Fact]
    public async Task 未設定の件数と閾値と期限は_presence_を立てない()
    {
        var (metrics, probe) = NewProbe();
        using var _ = probe;
        var fake = new FakeClient(new Pb.AcceptResponse());

        await Notifier(fake, metrics).NotifyAsync("owner-a", Kind, Occurred,
            ct: TestContext.Current.CancellationToken);

        fake.LastRequest!.HasCount.Should().BeFalse();
        fake.LastRequest.HasThresholdPercent.Should().BeFalse();
        fake.LastRequest.Deadline.Should().BeNull();
    }

    // 🔴 T-07 陽性対照。**設定した項目は presence が立ち、値が保たれる。**
    // T-06 だけでは「何も載せない」実装が合格してしまう。
    [Fact]
    public async Task 設定された件数と閾値と期限は_presence_が立つ()
    {
        var (metrics, probe) = NewProbe();
        using var _ = probe;
        var fake = new FakeClient(new Pb.AcceptResponse());
        var deadline = Occurred.AddDays(7);

        await Notifier(fake, metrics).NotifyAsync("owner-a", Kind, Occurred,
            count: 0, thresholdPercent: 80, deadline: deadline,
            ct: TestContext.Current.CancellationToken);

        fake.LastRequest!.HasCount.Should().BeTrue("★ 0 も「値」である（未設定ではない）");
        fake.LastRequest.Count.Should().Be(0);
        fake.LastRequest.HasThresholdPercent.Should().BeTrue();
        fake.LastRequest.ThresholdPercent.Should().Be(80);
        fake.LastRequest.Deadline!.ToDateTimeOffset().Should().Be(deadline);
        fake.LastRequest.OccurredAt.ToDateTimeOffset().Should().Be(Occurred);
    }

    // 🔴 T-08: **面が運ぶのは 6 項目ちょうどで、自由文の項目は 1 つも無い**（FR-22 受け入れ基準）。
    // REST 版の同名の試験（送る JSON の項目名を数える）と**同じ点**を proto の記述子で測る。
    [Fact]
    public void 面の項目は6つで自由文の項目を持たない()
    {
        var names = Pb.AcceptRequest.Descriptor.Fields.InDeclarationOrder()
            .Select(f => f.Name).ToList();

        names.Should().BeEquivalentTo(
            ["subject", "kind", "occurred_at", "count", "threshold_percent", "deadline"],
            "6 項目ちょうど。自由文の項目は 1 つも無い");
        string[] freeText = ["title", "body", "message", "text", "summary", "detail", "content"];
        names.Should().NotIntersectWith(freeText);
    }

    // ── 3. 期限（REST と同じ値を参照する） ──────────────────────────────────

    // 🔴 T-09: 送出 1 回あたりの期限は **`HttpPrivateNoteNotifier.SendTimeout` をそのまま引く** ——
    // 値を書き写すと片方だけ動いたときに気付けない。既定の 100 秒のままだと、受け口が応答しない間
    // 同期 push や完全削除の要求が止まる（fail-open は「待たせない」ことも要る）。
    [Fact]
    public async Task 送出の期限は_REST_と同じ値である()
    {
        var (metrics, probe) = NewProbe();
        using var _ = probe;
        var now = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        var fake = new FakeClient(new Pb.AcceptResponse());

        await Notifier(fake, metrics, new FixedClock(now)).NotifyAsync(
            "owner-a", Kind, Occurred, ct: TestContext.Current.CancellationToken);

        fake.LastOptions.Deadline.Should().Be(
            now.UtcDateTime.Add(HttpPrivateNoteNotifier.SendTimeout));
        HttpPrivateNoteNotifier.SendTimeout.Should().BeLessThan(TimeSpan.FromSeconds(100));
    }

    // ── 4. 切替は構成の有無だけである ────────────────────────────────────────

    // 🔴 T-10: `Services:NotificationServiceGrpc` が無ければ生成クライアントを**1 つも登録しない** ——
    // 登録の有無で `Program.cs` が REST 実装と gRPC 実装を選ぶ（並走中の正は REST。
    // 戻すのは構成を外すだけでコードは変えない）。
    //
    // 🔴 **`Program.cs` の DI をテストホストの構成で切り替えて測ることはできない。**
    // 選択は組み立て時（`builder.Configuration[...]`）に行われ、`WebApplicationFactory` が
    // 差し込む構成は Build 時に載るためである。したがって**登録関数そのもの**を対で固定する。
    [Fact]
    public void 宛先が未設定なら生成クライアントを登録しない()
    {
        var services = new ServiceCollection()
            .AddNotificationIngressGrpcClient(new ConfigurationBuilder().Build());

        services.Should().NotContain(
            d => d.ServiceType == typeof(Pb.NotificationIngress.NotificationIngressClient),
            "未設定なら何も登録しない（REST のまま）");
    }

    [Fact]
    public void 宛先が構成されていれば生成クライアントを登録する()
    {
        var services = new ServiceCollection().AddNotificationIngressGrpcClient(Configured());

        services.Should().Contain(
            d => d.ServiceType == typeof(Pb.NotificationIngress.NotificationIngressClient),
            "★ 陽性対照 —— 登録されないのは関数が壊れているからではない");
    }

    // 🔴 T-11: **チャネルは宛先ごとに 1 本**（[[IADR-0402]] 決定 6 / [[IADR-0412]] 決定 5）。
    // `TryAdd` なので 2 度呼んでも 1 本のままである —— `Add` にすると登録順で 2 本張られ、
    // `GetRequiredKeyedService` は最後の登録を返すので**障害としては現れず規約だけが静かに破れる**。
    [Fact]
    public void 同じ宛先へ2本目のチャネルを張らない()
    {
        var config = Configured();
        var services = new ServiceCollection()
            .AddNotificationIngressGrpcClient(config)
            .AddNotificationIngressGrpcClient(config);

        services.Count(d => d.ServiceType == typeof(GrpcChannel)
                         && Equals(d.ServiceKey, NotificationIngressGrpcClientExtensions.ChannelKey))
            .Should().Be(1);
    }

    // ── 器 ────────────────────────────────────────────────────────

    private static IConfiguration Configured() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [NotificationIngressGrpcClientExtensions.AddressKey] = "http://notification-service:8081",
        }).Build();

    private static GrpcPrivateNoteNotifier Notifier(
        Pb.NotificationIngress.NotificationIngressClient client,
        PrivateNoteNotificationMetrics metrics,
        TimeProvider? clock = null) =>
        new(client, metrics, clock ?? TimeProvider.System,
            NullLogger<GrpcPrivateNoteNotifier>.Instance);

    private static (PrivateNoteNotificationMetrics Metrics, CounterProbe Probe) NewProbe()
    {
        var factory = new ServiceCollection().AddMetrics().BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();
        var metrics = new PrivateNoteNotificationMetrics(factory);
        return (metrics, new CounterProbe(factory.Create(PrivateNoteNotificationMetrics.MeterName)));
    }

    // 実行時の計測を拾う。**Meter の「インスタンス」と計器名で絞る**（[[IADR-0394]] / #1275）——
    // `microservices-platform.document-service` は production の定数であり、同じ名前の Meter を
    // 別容器から作る試験クラスが並行して同じ計器を発行する（`ContainSingle()` が非決定的に破れる）。
    private sealed class CounterProbe : IDisposable
    {
        public List<(long Value, string? Kind, string? Outcome)> Measurements { get; } = [];
        private readonly MeterListener _listener;

        public CounterProbe(Meter meter)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (ReferenceEquals(instrument.Meter, meter)
                        && instrument.Name == PrivateNoteNotificationMetrics.DispatchCounterName)
                        listener.EnableMeasurementEvents(instrument);
                },
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                string? kind = null, outcome = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == PrivateNoteNotificationMetrics.KindTag) kind = tag.Value?.ToString();
                    if (tag.Key == PrivateNoteNotificationMetrics.OutcomeTag) outcome = tag.Value?.ToString();
                }
                Measurements.Add((value, kind, outcome));
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    // 既存の試験と同じ形の固定時計。**新しいライブラリは足さない**
    // （`scripts/backend-library-baseline.json` の ratchet に触れる）。
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeClient(Pb.AcceptResponse response)
        : Pb.NotificationIngress.NotificationIngressClient
    {
        public Pb.AcceptRequest? LastRequest { get; private set; }
        public CallOptions LastOptions { get; private set; }

        public override AsyncUnaryCall<Pb.AcceptResponse> AcceptAsync(
            Pb.AcceptRequest request, CallOptions options)
        {
            LastRequest = request;
            LastOptions = options;
            return new AsyncUnaryCall<Pb.AcceptResponse>(
                Task.FromResult(response), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
        }
    }

    private sealed class ThrowingClient(Exception exception)
        : Pb.NotificationIngress.NotificationIngressClient
    {
        public override AsyncUnaryCall<Pb.AcceptResponse> AcceptAsync(
            Pb.AcceptRequest request, CallOptions options) =>
            new(Task.FromException<Pb.AcceptResponse>(exception), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
    }
}
