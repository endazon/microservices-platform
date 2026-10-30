using System.Collections.Concurrent;
using AwesomeAssertions;
using Knowledge.Contracts.Events;
using Knowledge.IntegrationTests.Fixtures;
using Knowledge.IntegrationTests.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using global::DocumentService.Domain;
using global::DocumentService.Domain.Ports;
using global::DocumentService.Infrastructure.Messaging;
using global::DocumentService.Infrastructure.Persistence;

namespace Knowledge.IntegrationTests.DocumentService;

// 🔴 FR-06, FR-19, ADR-0027 §決定「イベント発行の原子性は Wolverine の EF Core + PostgreSQL Transactional Outbox を標準とする」,
// ADR-0057 決定 1・3, IADR-0604 (#2042): **削除の確定と `DocumentDeleted` が同じトランザクションで確定する**ことを
// 実 PostgreSQL ＋ 実 RabbitMQ で測る。
//
// 被験体は本番の部品そのものである: 共通ヘルパ（`UsePlatformMessagingDefaults` ＋ `UsePlatformTransactionalOutbox`）、
// 本番の経路宣言（`RoutePlatformEvent<DocumentDeleted>`）、本番の発行口（`WolverineDocumentDeletedPublisher`）、
// 本番の DbContext（Migration 済みの `DocumentDbContext`）。DB の変更には削除の端点が書く削除の事実
// （`DocumentDeletionMark`）を使う。
//
// 3 つの性質（作業仕様書 20261023_2042 の A1〜A3）:
//   A1 確定すれば、台帳の行とイベントの両方が出る。送られた後は Outbox の表に行が残らない。
//   A2 確定が失敗すれば、イベントも Outbox の行も出ない。
//   A3 コミットの後・送出の前にプロセスが止まっても Outbox の表に残り、再起動したホストが送る。
//
// 試験ごとに専用の DB を作る（Outbox の表の行数を他の試験と混ぜずに数えるため）。
[Trait("Category", "Integration")]
public sealed class DocumentDeletedOutboxTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 実処理（ブローカ越しの 1 通）の予算。購読開始は `ListenerReadiness` が別に待つ。
    private static readonly TimeSpan Delivery = TimeSpan.FromSeconds(30);

    // 再起動したホストの耐久エージェントが Outbox の表を拾うまでの予算（既定のポーリングは 5 秒）。
    private static readonly TimeSpan Recovery = TimeSpan.FromSeconds(60);

    private async Task<string> CreateDatabaseAsync()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres, RequiredServices.Broker);
        var baseConnectionString = postgres.ConnectionString!;
        var name = $"docoutbox_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(baseConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\";", admin);
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        var cs = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = name }.ConnectionString;

        // 本番と同じスキーマ（Migration）にする。
        await using var db = new DocumentDbContext(new DbContextOptionsBuilder<DocumentDbContext>().UseNpgsql(cs).Options);
        await db.Database.MigrateAsync(Ct);
        return cs;
    }

    // 発行側のホスト。DocumentService の `Program.cs` と同じ順で共通ヘルパを呼ぶ。
    // 🔴 Solo: 1 ホストで耐久エージェント（Outbox の表の回収）を即時に動かす（リーダー選出を待たない）。
    private Task<IHost> StartPublisherAsync(string cs) =>
        Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddDbContext<DocumentDbContext>(o => o.UseNpgsql(cs));
                services.AddScoped<IDocumentDeletedPublisher, WolverineDocumentDeletedPublisher>();
            })
            .UseWolverine(opts =>
            {
                opts.ServiceName = "document-outbox-under-test";
                opts.Discovery.DisableConventionalDiscovery();
                opts.UsePlatformRabbitMq(rabbit.ConnectionString!).AutoProvision();
                opts.RoutePlatformEvent<DocumentDeleted>();
                opts.UsePlatformMessagingDefaults();
                opts.UsePlatformTransactionalOutbox(cs);
                opts.Durability.Mode = DurabilityMode.Solo;
            })
            .StartAsync(Ct);

    // 購読側のホスト。試験ごとに別のキューを `DocumentDeleted` の exchange へ束ねる。
    private async Task<(IHost Host, DeletedProbe Probe)> StartSubscriberAsync()
    {
        var probe = new DeletedProbe();
        var serviceName = $"outbox-probe-{Guid.NewGuid():N}";
        var queue = nameof(DocumentDeleted);
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = serviceName;
                opts.Discovery.DisableConventionalDiscovery().IncludeType<DeletedProbeHandler>();
                opts.Services.AddSingleton(probe);
                opts.UsePlatformRabbitMq(rabbit.ConnectionString!).AutoProvision()
                    .BindPlatformQueue<DocumentDeleted>(serviceName, queue);
                opts.ListenToPlatformQueue(serviceName, queue);
                opts.UsePlatformMessagingDefaults();
            })
            .StartAsync(Ct);
        await ListenerReadiness.WaitUntilAcceptingAsync(host.Services, WolverineExtensions.PlatformQueueName(serviceName, queue));
        return (host, probe);
    }

    private static async Task<long> OutboxRowsAsync(string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(
            $"select count(*) from {WolverineExtensions.OutboxSchemaName}.wolverine_outgoing_envelopes;", conn);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<bool> MarkExistsAsync(string cs, Guid id)
    {
        await using var db = new DocumentDbContext(new DbContextOptionsBuilder<DocumentDbContext>().UseNpgsql(cs).Options);
        return await db.DocumentDeletionMarks.AnyAsync(m => m.DocumentId == id, Ct);
    }

    // 送出が済めば Outbox の行は消える（耐久送出は送れた envelope を表から消す）。消えるまで待つ。
    private static async Task WaitOutboxDrainedAsync(string cs, TimeSpan budget)
    {
        var until = DateTime.UtcNow + budget;
        while (await OutboxRowsAsync(cs) > 0)
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException($"Outbox の表が {budget.TotalSeconds:F0} 秒以内に空にならなかった（送出が済んでいない）。");
            await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        }
    }

    // A1: 確定すれば、台帳の行とイベントの両方が出る。
    [Fact]
    public async Task 確定すると台帳の行と削除イベントの両方が出る()
    {
        var cs = await CreateDatabaseAsync();
        var (subscriber, probe) = await StartSubscriberAsync();
        using var _ = subscriber;
        using var publisher = await StartPublisherAsync(cs);
        var id = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;

        await using (var scope = publisher.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            var deleted = scope.ServiceProvider.GetRequiredService<IDocumentDeletedPublisher>();
            db.DocumentDeletionMarks.Add(DocumentDeletionMark.For(id, at));
            await deleted.PublishDeletedAsync(id, at, ct: Ct);
            await deleted.SaveChangesAndFlushAsync(Ct);
        }

        (await probe.Received(id).WaitAsync(Delivery, Ct)).Should().Be(id);
        (await MarkExistsAsync(cs, id)).Should().BeTrue("DB の変更も同じ確定でコミットされている");
        await WaitOutboxDrainedAsync(cs, Delivery);
        probe.CountFor(id).Should().Be(1);
    }

    // A2: 確定が失敗すれば、イベントも Outbox の行も出ない。
    //
    // 失敗は「削除の事実の主キーの重複」で起こす（実 PostgreSQL の制約違反。トランザクションは中断状態になる）。
    // 否定（来ないこと）を時間待ちで測らないため、同じスコープで**後から**正しい削除を 1 件確定させ、それが届いた時点で
    // 先の 1 件が届いていないことを確かめる（同じ exchange・同じキューを同じ順で通る）。
    // 🔴 失敗時の巻き戻しを外すと、**確定しなかった 1 件目のイベントが 2 件目の確定と一緒に送られる**（変異で確認。
    // EF はユーザーのトランザクション内の `SaveChanges` の前にセーブポイントを置き、失敗するとそこまでしか戻さない。
    // envelope の INSERT はセーブポイントより前なので生き残り、2 件目のコミットで確定してしまう）。
    [Fact]
    public async Task DBの確定が失敗すれば削除イベントは出ない()
    {
        var cs = await CreateDatabaseAsync();
        var failed = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        await using (var seed = new DocumentDbContext(new DbContextOptionsBuilder<DocumentDbContext>().UseNpgsql(cs).Options))
        {
            seed.DocumentDeletionMarks.Add(DocumentDeletionMark.For(failed, at));
            await seed.SaveChangesAsync(Ct);
        }
        var (subscriber, probe) = await StartSubscriberAsync();
        using var _ = subscriber;
        using var publisher = await StartPublisherAsync(cs);
        var sentinel = Guid.NewGuid();

        await using (var scope = publisher.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            var deleted = scope.ServiceProvider.GetRequiredService<IDocumentDeletedPublisher>();

            db.DocumentDeletionMarks.Add(DocumentDeletionMark.For(failed, at));
            await deleted.PublishDeletedAsync(failed, at, ct: Ct);
            var act = () => deleted.SaveChangesAndFlushAsync(Ct);
            await act.Should().ThrowAsync<DbUpdateException>("主キーの重複で確定が失敗する");
            (await OutboxRowsAsync(cs)).Should().Be(0, "確定が失敗すれば Outbox の行も巻き戻る");

            // 失敗した変更を捨てて、同じスコープ（同じ Outbox）で次の削除を確定する。
            db.ChangeTracker.Clear();
            db.DocumentDeletionMarks.Add(DocumentDeletionMark.For(sentinel, at));
            await deleted.PublishDeletedAsync(sentinel, at, ct: Ct);
            await deleted.SaveChangesAndFlushAsync(Ct);
        }

        (await probe.Received(sentinel).WaitAsync(Delivery, Ct)).Should().Be(sentinel);
        await WaitOutboxDrainedAsync(cs, Delivery);
        probe.CountFor(failed).Should().Be(0, "確定しなかった削除のイベントは送られない（後の確定にも混ざらない）");
    }

    // 🔴 IADR-0657 (#2361): **送出表への書き込みが失敗すれば、確定全体が例外で巻き戻る**（削除の事実も残らない。欠落しない）。
    // 素の `IMessageBus.PublishAsync` と違い、`IDbContextOutbox` は envelope をアプリのトランザクションの中で書くので、
    // JasperFx の `RetryBlock`（失敗を例外にせず裏で再試行し、上限で黙って捨てる）を通らない。その固定である。
    // 失敗は専用の DB の送出表に置いたトリガで起こす（`OutboxStoreFailure`。片付けは finally）。
    [Fact]
    public async Task 送出表への書き込みが失敗すれば確定全体が例外で巻き戻る()
    {
        var cs = await CreateDatabaseAsync();
        using var publisher = await StartPublisherAsync(cs);
        var id = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;

        await OutboxStoreFailure.InstallAsync(cs, Ct);
        try
        {
            await using var scope = publisher.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            var deleted = scope.ServiceProvider.GetRequiredService<IDocumentDeletedPublisher>();
            var act = async () =>
            {
                db.DocumentDeletionMarks.Add(DocumentDeletionMark.For(id, at));
                await deleted.PublishDeletedAsync(id, at, ct: Ct);
                await deleted.SaveChangesAndFlushAsync(Ct);
            };
            (await act.Should().ThrowAsync<Exception>("送出表への書き込みの失敗は確定の失敗である"))
                .Which.ToString().Should().Contain(OutboxStoreFailure.Message);
        }
        finally
        {
            await OutboxStoreFailure.RemoveAsync(cs);
        }

        (await MarkExistsAsync(cs, id)).Should().BeFalse("削除の事実も同じ確定で巻き戻る（削除をやり直せる）");
        (await OutboxRowsAsync(cs)).Should().Be(0);
    }

    // A3: コミットの後・送出の前に止まっても Outbox の表に残り、再起動したホストが送る。
    //
    // 「コミットの後・送出の前のプロセス終了」を、発行口が内部で行う 2 段（DB のコミット → 送出）の 1 段目だけを
    // 実行して作る（`IDbContextOutbox` はスコープ内で発行口と同じインスタンスである）。その後ホストを止め、
    // 同じ DB を向いた新しいホストを起こす —— 新しいホストの耐久エージェントが表から拾って送る。
    // 🔴 耐久送出（`UseDurableOutboxOnAllSendingEndpoints`）を外すと、発行は表へ書かれず（行数 0）ここで赤になる。
    [Fact]
    public async Task 確定の後に送出が失われても再起動後に再送される()
    {
        var cs = await CreateDatabaseAsync();
        var (subscriber, probe) = await StartSubscriberAsync();
        using var _ = subscriber;
        var id = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;

        using (var crashed = await StartPublisherAsync(cs))
        {
            await using (var scope = crashed.Services.CreateAsyncScope())
            {
                var deleted = scope.ServiceProvider.GetRequiredService<IDocumentDeletedPublisher>();
                var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<DocumentDbContext>>();
                outbox.DbContext.DocumentDeletionMarks.Add(DocumentDeletionMark.For(id, at));
                await deleted.PublishDeletedAsync(id, at, ct: Ct);

                // コミットだけを行い、送出（FlushOutgoingMessagesAsync）へ進まない。
                await outbox.DbContext.SaveChangesAsync(Ct);
                await outbox.DbContext.Database.CommitTransactionAsync(Ct);
            }

            (await MarkExistsAsync(cs, id)).Should().BeTrue("DB の変更はコミット済み");
            (await OutboxRowsAsync(cs)).Should().Be(1, "送られていないイベントは Outbox の表（台帳）に残る");
            await crashed.StopAsync(Ct);
        }

        using var restarted = await StartPublisherAsync(cs);
        (await probe.Received(id).WaitAsync(Recovery, Ct)).Should().Be(id, "再起動したホストが Outbox の表から送る");
        await WaitOutboxDrainedAsync(cs, Delivery);
    }

    public sealed class DeletedProbe
    {
        private readonly ConcurrentDictionary<Guid, int> _counts = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<Guid>> _waiters = new();

        public void Record(Guid id)
        {
            _counts.AddOrUpdate(id, 1, (_, n) => n + 1);
            Waiter(id).TrySetResult(id);
        }

        public int CountFor(Guid id) => _counts.TryGetValue(id, out var n) ? n : 0;

        public Task<Guid> Received(Guid id) => Waiter(id).Task;

        private TaskCompletionSource<Guid> Waiter(Guid id) =>
            _waiters.GetOrAdd(id, _ => new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    public sealed class DeletedProbeHandler
    {
        public void Handle(DocumentDeleted message, DeletedProbe probe) => probe.Record(message.DocumentId);
    }
}
