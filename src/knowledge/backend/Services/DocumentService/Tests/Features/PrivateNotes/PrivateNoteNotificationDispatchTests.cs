using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Grpc.Core;
using DocumentService.Domain;
using DocumentService.Features.ObsidianSync;
using DocumentService.Common.Observability;
using DocumentService.Infrastructure.Persistence;
using DocumentService.Domain.Ports;
using DocumentService.Features.PrivateNotes;
using DocumentService.Infrastructure.ExternalServices;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using DocumentService.Features.ObsidianSync.Push;
using DocumentService.Features.PrivateNotes.Maintenance;
using Pb = Platform.Shared.Contracts.Grpc.Notification.V1;

namespace DocumentService.Tests.Features.PrivateNotes;

// FR-22, FR-19, FR-20, NFR-19, ADR-0037 決定 6・17・18, ADR-0045 決定 8,
// IADR-0215 決定 5-a・5-b（2026-08-28 追記 / #600）, [[IADR-0270]] 決定 6:
// **通知の発火の結線**（#600 トラック 3E）。
//
// 既存の 3 契機のテスト（PrivateNoteLifecycleTests / PrivateNoteQuotaTests / SyncDeviceTokenTests）は
// **記録用スタブを相手にしている**ため、「発火したか」しか見ていない。本クラスが見るのはその先である。
//
// 1. **業務経路の fail-open**（受け口が落ちていても業務処理を失敗させない。**任意の例外**を含む）
// 2. **順序＝冪等性**（発火記録が送出より先に確定していること。再起動で重複発火しない）
//
// ［2026-10-10 / #1255］[[IADR-0533]]: REST のアダプタ（`HttpPrivateNoteNotifier`）を撤去したので、アダプタ単体の表明
// （送出の面・握る範囲・呼び出し元のキャンセル・計器・名前付きクライアントの期限）は撤去した。gRPC のアダプタの同じ表明は
// `GrpcPrivateNoteNotifierTests`（T-01〜T-09）が持つ。業務経路の fail-open は gRPC のアダプタで測り直した。
[Trait("TestKind", "Integration")]
public class PrivateNoteNotificationDispatchTests
{
    // ── 1. 業務経路の fail-open（実アダプタ＋到達できない受け口） ────────────────

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

    // ── 2. 順序＝冪等性（発火記録が送出より先に確定している） ────────────────────

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
                services.RemoveAll<Pb.NotificationIngress.NotificationIngressClient>();
                services.AddSingleton<Pb.NotificationIngress.NotificationIngressClient>(new UnreachableIngressClient());
                services.AddScoped<IPrivateNoteNotifier, GrpcPrivateNoteNotifier>();
            });
        }
    }

    // 常に到達できない受け口。**`RpcException` ではない例外**を投げる（fail-open の主眼。型の列挙から漏れる例外）。
    private sealed class UnreachableIngressClient : Pb.NotificationIngress.NotificationIngressClient
    {
        public override AsyncUnaryCall<Pb.AcceptResponse> AcceptAsync(Pb.AcceptRequest request, CallOptions options) =>
            throw new InvalidOperationException("受け口が配備されていない");
    }
}
