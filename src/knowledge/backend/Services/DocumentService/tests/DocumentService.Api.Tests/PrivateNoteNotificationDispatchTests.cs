using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using DocumentService.Api.Foundation.Domain;
using DocumentService.Api.Foundation.Endpoints;
using DocumentService.Api.Foundation.Observability;
using DocumentService.Api.Foundation.Persistence;
using DocumentService.Api.Foundation.Ports;
using DocumentService.Api.Foundation.Services;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocumentService.Api.Tests;

// FR-22, FR-19, FR-20, NFR-19, ADR-0037 決定 6・17・18, ADR-0045 決定 8,
// IADR-0215 決定 5-a・5-b（2026-08-28 追記 / #600）, [[IADR-0270]] 決定 6:
// **通知の発火の結線**（#600 トラック 3E）。
//
// 既存の 3 契機のテスト（PrivateNoteLifecycleTests / PrivateNoteQuotaTests / SyncDeviceTokenTests）は
// **記録用スタブを相手にしている**ため、「発火したか」しか見ていない。本クラスが見るのはその先である。
//
// 1. **送出の面**（パスと、送る JSON が件数・閾値・期限だけであること）
// 2. **fail-open**（受け口が落ちていても業務処理を失敗させない。**任意の例外**を含む）
// 3. **順序＝冪等性**（発火記録が送出より先に確定していること。再起動で重複発火しない）
// 4. **計器**（届かなかったことが数えられる。利用者識別子は属性にしない）
public class PrivateNoteNotificationDispatchTests
{
    // ── 1. 送出の面 ────────────────────────────────────────────────────────

    // FR-22 受け入れ基準: **本文は件数と期限のみ**。ポートの型に自由文が無いことは
    // IPrivateNoteNotifier のシグネチャが守っているが、**アダプタが実際に送る JSON** でも固定する
    // （匿名オブジェクトへ 1 項目足すだけで自由文が混入し得るため、型だけでは足りない）。
    [Fact]
    public async Task 送出先のパスと本文は件数と閾値と期限だけで構成される()
    {
        var handler = new FakeIngressHandler { Response = () => new HttpResponseMessage(HttpStatusCode.Created) };
        var (notifier, _) = BuildNotifier(handler);

        await notifier.NotifyAsync("owner-a", PrivateNoteNotificationKinds.PrivateNotePurgeImminent,
            DateTimeOffset.UtcNow, count: 3, deadline: DateTimeOffset.UtcNow.AddDays(7),
            ct: TestContext.Current.CancellationToken);

        handler.LastPath.Should().Be(HttpPrivateNoteNotifier.IngressPath,
            "★ 受け口 NotificationIngressEndpoints と 1 バイトでも違えば通知は届かない");

        using var body = JsonDocument.Parse(handler.LastBody!);
        var names = body.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        names.Should().BeEquivalentTo(
            ["subject", "kind", "occurredAt", "count", "thresholdPercent", "deadline"],
            "6 項目ちょうど。自由文の項目は 1 つも無い");
        string[] freeText = ["title", "body", "message", "text", "summary", "detail", "content"];
        names.Should().NotIntersectWith(freeText, "自由文に相当する項目が 1 つも無い");
    }

    // ── 2. fail-open（受け口が落ちていても業務処理を止めない） ──────────────────

    // FR-22, IADR-0215 決定 5-b: **握るのは呼び出し元のキャンセル以外のすべて**である。
    // 🔴 従前の実装は HttpRequestException と TaskCanceledException の 2 型だけを握っており、
    // **列挙から漏れた例外が呼び出し元（同期 push・完全削除・定期処理）へ抜けた**。
    [Theory]
    [InlineData("http")]           // 通信エラー
    [InlineData("timeout")]        // タイムアウト（呼び出し元のキャンセルではない）
    [InlineData("invalid-op")]     // ★ 型の列挙から漏れる例外（この行が変異試験の主眼）
    [InlineData("not-supported")]  // 同上
    [InlineData("status-500")]
    [InlineData("status-404")]
    public async Task 受け口が落ちていても送出は例外を投げない(string mode)
    {
        var handler = new FakeIngressHandler
        {
            Response = mode switch
            {
                "http" => () => throw new HttpRequestException("接続できない"),
                "timeout" => () => throw new TaskCanceledException("タイムアウト"),
                "invalid-op" => () => throw new InvalidOperationException("BaseAddress 不整合"),
                "not-supported" => () => throw new NotSupportedException("想定外"),
                "status-500" => () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                _ => () => new HttpResponseMessage(HttpStatusCode.NotFound),
            },
        };
        var (notifier, _) = BuildNotifier(handler);

        var act = async () => await notifier.NotifyAsync("owner-a",
            PrivateNoteNotificationKinds.StorageQuotaWarning, DateTimeOffset.UtcNow,
            thresholdPercent: 80);

        await act.Should().NotThrowAsync("通知は本体操作の従属物ではない（IADR-0215 決定 3 の発火側の対）");
    }

    // IADR-0215 決定 5-b: **呼び出し元のキャンセルだけは伝播させる。**
    // 握ると「キャンセルされたのに続行した」ように見える（シャットダウン・利用者の切断は
    // 業務処理の側の事情であって、通知の失敗ではない）。
    [Fact]
    public async Task 呼び出し元のキャンセルは握り潰さない()
    {
        var handler = new FakeIngressHandler
        {
            Response = () => throw new TaskCanceledException("キャンセル"),
        };
        var (notifier, _) = BuildNotifier(handler);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await notifier.NotifyAsync("owner-a",
            PrivateNoteNotificationKinds.SyncTokenExpiry, DateTimeOffset.UtcNow, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── 3. 計器（ADR-0045 決定 8「静かに落ちない」の発火側の対） ─────────────────

    [Theory]
    [InlineData("created", PrivateNoteNotificationMetrics.OutcomeSent)]
    [InlineData("status-500", PrivateNoteNotificationMetrics.OutcomeRejected)]
    [InlineData("http", PrivateNoteNotificationMetrics.OutcomeUnreachable)]
    public async Task 送出の結末が計器に載る(string mode, string expectedOutcome)
    {
        var handler = new FakeIngressHandler
        {
            Response = mode switch
            {
                "created" => () => new HttpResponseMessage(HttpStatusCode.Created),
                "status-500" => () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                _ => () => throw new HttpRequestException("接続できない"),
            },
        };
        var (notifier, probe) = BuildNotifier(handler);

        await notifier.NotifyAsync("owner-a", PrivateNoteNotificationKinds.PrivateNotePurgeWeekly,
            DateTimeOffset.UtcNow, count: 2, ct: TestContext.Current.CancellationToken);

        probe.Measurements.Should().ContainSingle();
        var m = probe.Measurements[0];
        m.Value.Should().Be(1);
        m.Tags[PrivateNoteNotificationMetrics.OutcomeTag].Should().Be(expectedOutcome);
        m.Tags[PrivateNoteNotificationMetrics.KindTag]
            .Should().Be(PrivateNoteNotificationKinds.PrivateNotePurgeWeekly);
        // 🔴 利用者識別子を属性にしない（非有界のカーディナリティ・個人の利用行動の記録）。
        m.Tags.Should().HaveCount(2);
        m.Tags.Values.Should().NotContain("owner-a");
    }

    // ── 4. タイムアウトの配線（実ホストの登録を見る） ────────────────────────

    // IADR-0215 決定 5-b: 既定の 100 秒では、受け口が応答しないときに**利用者の要求がその間止まる**。
    // 定数の宣言ではなく **Program.cs の登録**を見る（定数だけ見ると配線漏れが緑で通る）。
    [Fact]
    public void 送出クライアントのタイムアウトは既定の100秒ではない()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();

        var client = factory.Services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(HttpPrivateNoteNotifier.ClientName);

        client.Timeout.Should().Be(HttpPrivateNoteNotifier.SendTimeout);
        client.Timeout.Should().BeLessThan(TimeSpan.FromSeconds(100), "既定のままにしない");
    }

    // ── 5. 業務経路の fail-open（実アダプタ＋到達できない受け口） ────────────────

    // FR-19, FR-20, FR-22: **受け口へ到達できなくても同期 push は成功する。**
    // 実アダプタを差した器で行う（スタブ相手のテストでは、アダプタの握る範囲を見られない）。
    [Fact]
    public async Task 受け口へ到達できなくても同期pushと完全削除は成功する()
    {
        using var factory = new UnreachableIngressWebApplicationFactory();
        var user = $"unreach-{Guid.NewGuid():N}"[..24];
        var session = factory.CreateClient();
        session.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);

        var issued = await session.PostAsJsonAsync("/private-notes/devices", new { deviceName = "pc" }, TestContext.Current.CancellationToken);
        var token = (await issued.Content.ReadFromJsonAsync<SyncTokenIssuedResponse>(TestContext.Current.CancellationToken))!.Token;
        var plugin = factory.CreateClient();
        plugin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "admin");
        await admin.PutAsJsonAsync($"/private-notes/quotas/{user}", new { limitBytes = 1_000L }, TestContext.Current.CancellationToken);

        // 850 バイト = 85% → 容量警告が発火する（＝送出が必ず起きる経路）
        var push = await plugin.PostAsJsonAsync("/private-notes/sync/notes", new
        {
            vaultPath = "unreachable.md",
            title = "到達不能",
            edits = new[] { new { content = new string('a', 850) } },
        }, TestContext.Current.CancellationToken);
        push.StatusCode.Should().Be(HttpStatusCode.Created,
            "★ 受け口が落ちていても資料は保存される（通知は本体操作の従属物ではない）");

        var note = (await push.Content.ReadFromJsonAsync<PushNoteResponse>(TestContext.Current.CancellationToken))!.NoteId;
        (await session.DeleteAsync($"/private-notes/{note}", TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        var purge = await session.PostAsJsonAsync("/private-notes/purge", new { ids = new[] { note } }, TestContext.Current.CancellationToken);
        purge.StatusCode.Should().Be(HttpStatusCode.OK, "完全削除も同じく止まらない");

        // 定期処理も落ちない（HostedService が周期ごとに落ちると通知が永久に出なくなる）。
        using var scope = factory.Services.CreateScope();
        var maintenance = scope.ServiceProvider.GetRequiredService<PrivateNoteMaintenanceService>();
        await maintenance.Invoking(m => m.RunAsync(DateTimeOffset.UtcNow))
            .Should().NotThrowAsync();
    }

    // ── 6. 順序＝冪等性（発火記録が送出より先に確定している） ────────────────────

    // FR-22, FR-19, IADR-0215 決定 5-a: **「各 1 回」は送出と記録の順序で決まる。**
    // 逆順（送出 → 記録 → 保存）だと、送出後・保存前にプロセスが落ちたとき次周期で重複して送る。
    // 🔴 受け口の重複抑止はペイロード 6 項目の完全一致でしか畳まず、`occurredAt` が変わる再検知は
    // 畳まれない —— **重複を止められるのは発火側だけ**である。
    [Fact]
    public async Task 削除通知とトークン期限予告の発火記録は送出より先に確定している()
    {
        using var factory = new OrderProbingWebApplicationFactory();
        var user = $"order-{Guid.NewGuid():N}"[..24];
        var now = DateTimeOffset.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            // ①-b: 残り 5 日（7 日前の窓内）／①-a: 論理削除済みなので週次の対象でもある
            var doc = Document.Create("予告", originalUri: null, contentType: "text/plain");
            db.Documents.Add(doc);
            var note = PrivateNote.Create(doc.Id, user, "soon.md", 100, "hash", now.AddDays(-85));
            note.SoftDelete(now.AddDays(-85));
            db.PrivateNotes.Add(note);
            // ③: 残り 5 日のトークン
            db.SyncDevices.Add(SyncDevice.Create(user, "expiring", SyncTokens.Generate().Hash,
                now.AddDays(-25)));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<PrivateNoteMaintenanceService>()
                .RunAsync(now, TestContext.Current.CancellationToken);
        }

        var sent = factory.Probe.Notifications.Where(n => n.Subject == user).ToList();
        sent.Select(n => n.Kind).Should().BeEquivalentTo([
            PrivateNoteNotificationKinds.PrivateNotePurgeImminent,
            PrivateNoteNotificationKinds.PrivateNotePurgeWeekly,
            PrivateNoteNotificationKinds.SyncTokenExpiry,
        ], "①-b・①-a・③ の 3 契機が発火する");

        sent.Should().OnlyContain(n => n.RecordAlreadyPersisted,
            "★ 送出の瞬間に、発火記録は既に永続化されていなければならない");
        // FR-22: 宛先は所有者本人のみ（他人の subject が混ざらない）。
        factory.Probe.Notifications.Should().OnlyContain(n => n.Subject == user);
    }

    // FR-19 受け入れ基準 ④, FR-22 ②, IADR-0215 決定 5-a: 容量警告も同じ順序で送る。
    // ②の発火記録（Warned80 / Warned95）が保存前に送られると、再計算のたびに重複して送られ得る。
    [Fact]
    public async Task 容量警告の発火記録は送出より先に確定している()
    {
        using var factory = new OrderProbingWebApplicationFactory();
        var user = $"quota-order-{Guid.NewGuid():N}"[..24];
        var session = factory.CreateClient();
        session.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        var issued = await session.PostAsJsonAsync("/private-notes/devices", new { deviceName = "pc" }, TestContext.Current.CancellationToken);
        var token = (await issued.Content.ReadFromJsonAsync<SyncTokenIssuedResponse>(TestContext.Current.CancellationToken))!.Token;
        var plugin = factory.CreateClient();
        plugin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "admin");
        await admin.PutAsJsonAsync($"/private-notes/quotas/{user}", new { limitBytes = 1_000L }, TestContext.Current.CancellationToken);

        var push = await plugin.PostAsJsonAsync("/private-notes/sync/notes", new
        {
            vaultPath = "warn.md",
            title = "警告",
            edits = new[] { new { content = new string('a', 850) } },
        }, TestContext.Current.CancellationToken);
        push.StatusCode.Should().Be(HttpStatusCode.Created);

        var warnings = factory.Probe.Notifications
            .Where(n => n.Subject == user
                && n.Kind == PrivateNoteNotificationKinds.StorageQuotaWarning).ToList();
        warnings.Should().ContainSingle().Which.ThresholdPercent.Should().Be(80);
        warnings[0].RecordAlreadyPersisted.Should().BeTrue(
            "★ Warned80 が確定してから送る（再計算での重複発火を止める）");
        warnings[0].Count.Should().BeNull("②は件数を持たない（閾値のみ）");
    }

    // ── 補助 ────────────────────────────────────────────────────────────

    private static (HttpPrivateNoteNotifier Notifier, MetricsProbe Probe) BuildNotifier(
        HttpMessageHandler handler)
    {
        var meterFactory = new TestMeterFactory();
        var metrics = new PrivateNoteNotificationMetrics(meterFactory);
        var probe = new MetricsProbe(meterFactory.CreatedMeterName!);
        var notifier = new HttpPrivateNoteNotifier(
            new SingleClientHttpClientFactory(handler),
            metrics,
            NullLogger<HttpPrivateNoteNotifier>.Instance);
        return (notifier, probe);
    }

    private sealed class FakeIngressHandler : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Response { get; init; }
            = () => new HttpResponseMessage(HttpStatusCode.Created);

        public string? LastPath { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return Response();
        }
    }

    // 常に到達できない受け口。**型の列挙から漏れる例外**を投げる（fail-open の主眼）。
    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("受け口が配備されていない");
    }

    private sealed class SingleClientHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://notification-service:8080"),
            Timeout = HttpPrivateNoteNotifier.SendTimeout,
        };
    }

    // Meter 名へ一意な接尾辞を付ける（テスト間の測定の混入を構造的に防ぐ。IngestTagFilterTests と同じ作法）。
    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];
        public string? CreatedMeterName { get; private set; }

        public Meter Create(MeterOptions options)
        {
            CreatedMeterName = $"{options.Name}.test-{Guid.NewGuid():N}";
            var meter = new Meter(CreatedMeterName, options.Version, options.Tags, scope: this);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var m in _meters) m.Dispose();
            _meters.Clear();
        }
    }

    private sealed class MetricsProbe
    {
        public record Measurement(long Value, IReadOnlyDictionary<string, string?> Tags);

        private readonly List<Measurement> _measurements = [];

        public MetricsProbe(string meterName)
        {
            var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == meterName
                        && instrument.Name == PrivateNoteNotificationMetrics.DispatchCounterName)
                        l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                var map = new Dictionary<string, string?>();
                foreach (var t in tags) map[t.Key] = t.Value?.ToString();
                lock (_measurements) _measurements.Add(new Measurement(value, map));
            });
            listener.Start();
        }

        public IReadOnlyList<Measurement> Measurements
        {
            get { lock (_measurements) return [.. _measurements]; }
        }
    }

    // 送出の瞬間に**別スコープの DbContext** で発火記録を読み、確定済みかどうかを記録する。
    // EF InMemory は SaveChanges でストアへ書くため、**未保存の変更はこの読みに現れない** ——
    // 「記録が先か送出が先か」を決定的に観測できる。
    private sealed class OrderProbingPrivateNoteNotifier(IServiceScopeFactory scopes)
        : IPrivateNoteNotifier
    {
        public record Sent(string Subject, string Kind, int? Count, int? ThresholdPercent,
            DateTimeOffset? Deadline, bool RecordAlreadyPersisted);

        private readonly List<Sent> _sent = [];

        public IReadOnlyList<Sent> Notifications
        {
            get { lock (_sent) return [.. _sent]; }
        }

        public Task NotifyAsync(string subject, string kind, DateTimeOffset occurredAt,
            int? count = null, int? thresholdPercent = null, DateTimeOffset? deadline = null,
            CancellationToken ct = default)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            var quota = db.PrivateNoteQuotas.FirstOrDefault(q => q.OwnerId == subject);

            var persisted = kind switch
            {
                PrivateNoteNotificationKinds.PrivateNotePurgeImminent =>
                    db.PrivateNotes.Any(n => n.OwnerId == subject && n.PurgeImminentNotifiedAt != null),
                PrivateNoteNotificationKinds.PrivateNotePurgeWeekly =>
                    quota?.WeeklyDigestSentAt is not null,
                PrivateNoteNotificationKinds.SyncTokenExpiry =>
                    db.SyncDevices.Any(d => d.OwnerId == subject && d.ExpiryNotifiedAt != null),
                PrivateNoteNotificationKinds.StorageQuotaWarning =>
                    thresholdPercent == PrivateNoteQuota.WarnPercentHigh
                        ? quota?.Warned95 == true
                        : quota?.Warned80 == true,
                // ①-c（事後通知）は発火記録を持たない —— 行そのものが消えるため構造的に 1 回である。
                _ => true,
            };
            lock (_sent)
                _sent.Add(new Sent(subject, kind, count, thresholdPercent, deadline, persisted));
            return Task.CompletedTask;
        }
    }

    private sealed class OrderProbingWebApplicationFactory : TestWebApplicationFactory
    {
        private OrderProbingPrivateNoteNotifier? _probe;

        public OrderProbingPrivateNoteNotifier Probe => _probe
            ?? (OrderProbingPrivateNoteNotifier)Services.GetRequiredService<IPrivateNoteNotifier>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPrivateNoteNotifier>();
                services.AddSingleton<IPrivateNoteNotifier>(sp =>
                    _probe = new OrderProbingPrivateNoteNotifier(
                        sp.GetRequiredService<IServiceScopeFactory>()));
            });
        }
    }

    // **実アダプタ**を使い、受け口へは到達できない器。スタブ相手では握る範囲を見られない。
    private sealed class UnreachableIngressWebApplicationFactory : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPrivateNoteNotifier>();
                services.AddScoped<IPrivateNoteNotifier, HttpPrivateNoteNotifier>();
                services.AddHttpClient(HttpPrivateNoteNotifier.ClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new UnreachableHandler());
            });
        }
    }
}
