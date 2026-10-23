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
using Knowledge.Contracts.Dtos;
using global::ConversionService.Domain.Ports;
using global::ConversionService.Infrastructure.Messaging;
using global::ConversionService.Infrastructure.Persistence;

namespace Knowledge.IntegrationTests.ConversionService;

// 🔴 FR-12 例外フロー, UC-06, ADR-0027 §決定「イベント発行の原子性は Wolverine の EF Core + PostgreSQL Transactional Outbox を標準とする」,
// IADR-0609 (#2169): **再変換の queued への遷移と `RawDocumentFetched` の再発行が同じトランザクションで確定する**ことを
// 実 PostgreSQL ＋ 実 RabbitMQ で測る（段 1 の `DocumentDeletedOutboxTests`・段 2 の `DocumentUpdatedOutboxTests` と同じ作法）。
//
// 被験体は本番の部品そのものである: 共通ヘルパ（`UsePlatformMessagingDefaults` ＋ `UsePlatformTransactionalOutbox`）、
// 本番の経路宣言（`RoutePlatformEvent<RawDocumentFetched>`）、本番の発行口（`WolverineConversionRetryPublisher`）、
// 本番のストア（`EfConversionJobStore.PrepareRetryAsync`）と Migration 済みの `ConversionJobDbContext`。
// 端点（`RetryConversionJobEndpoint`）と同じく、`CommitAsync` の関数の中でストアの遷移と発行を行う。
//
// 性質（作業仕様書 20261023_2169 の C1〜C4）:
//   C1 確定すれば、ジョブは queued になりイベントが出る。送られた後は Outbox の表に行が残らない。
//   C2 確定が失敗すれば、ジョブは failed のままでイベントも Outbox の行も出ない（同じスコープの後続の確定にも混ざらない）。
//   C3 コミットの後・送出の前にプロセスが止まっても Outbox の表に残り、再起動したホストが送る。
//   C4 外側のトランザクションが開いていれば確定せず例外にする。
//
// 試験ごとに専用の DB を作る（Outbox の表の行数を他の試験と混ぜずに数えるため）。
[Trait("Category", "Integration")]
public sealed class ConversionRetryOutboxTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 実処理（ブローカ越しの 1 通）の予算。購読開始は `ListenerReadiness` が別に待つ。
    private static readonly TimeSpan Delivery = TimeSpan.FromSeconds(30);

    // 再起動したホストの耐久エージェントが Outbox の表を拾うまでの予算（既定のポーリングは 5 秒）。
    private static readonly TimeSpan Recovery = TimeSpan.FromSeconds(60);

    private static RawDocumentFetched Raw(Guid id) =>
        new(id, Guid.NewGuid(), "filesystem", "/docs/retry.pdf", $"storage://{id:N}/raw",
            "application/pdf", new Dictionary<string, string> { ["confidentiality"] = "internal" }, [],
            DateTimeOffset.UtcNow);

    private static ConversionJobDbContext Open(string cs) =>
        new(new DbContextOptionsBuilder<ConversionJobDbContext>().UseNpgsql(cs).Options);

    private async Task<string> CreateDatabaseAsync()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres, RequiredServices.Broker);
        var baseConnectionString = postgres.ConnectionString!;
        var name = $"convoutbox_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(baseConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\";", admin);
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        var cs = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = name }.ConnectionString;

        // 本番と同じスキーマ（Migration）にする。
        await using var db = Open(cs);
        await db.Database.MigrateAsync(Ct);
        return cs;
    }

    // 失敗したジョブを 1 件置く（再変換の前提）。
    private static async Task SeedFailedAsync(string cs, Guid id)
    {
        await using var db = Open(cs);
        var store = new EfConversionJobStore(db);
        await store.StartAsync(Raw(id), Ct);
        await store.FailAsync(id, "変換に失敗した（試験の前提）", ct: Ct);
    }

    private static async Task<string?> StatusAsync(string cs, Guid id)
    {
        await using var db = Open(cs);
        return (await db.ConversionJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, Ct))?.Status;
    }

    // 発行側のホスト。ConversionService の `Program.cs` と同じ順で共通ヘルパを呼ぶ（購読の段は立てない）。
    // 🔴 Solo: 1 ホストで耐久エージェント（Outbox の表の回収）を即時に動かす（リーダー選出を待たない）。
    private Task<IHost> StartPublisherAsync(string cs) =>
        Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddDbContext<ConversionJobDbContext>(o => o.UseNpgsql(cs));
                services.AddScoped<IConversionJobStore, EfConversionJobStore>();
                services.AddScoped<IConversionRetryPublisher, WolverineConversionRetryPublisher>();
            })
            .UseWolverine(opts =>
            {
                opts.ServiceName = "conversion-outbox-under-test";
                opts.Discovery.DisableConventionalDiscovery();
                opts.UsePlatformRabbitMq(rabbit.ConnectionString!).AutoProvision();
                opts.RoutePlatformEvent<RawDocumentFetched>();
                opts.UsePlatformMessagingDefaults();
                opts.UsePlatformTransactionalOutbox(cs);
                opts.Durability.Mode = DurabilityMode.Solo;
            })
            .StartAsync(Ct);

    // 購読側のホスト。試験ごとに別のキューを `RawDocumentFetched` の exchange へ束ねる。
    private async Task<(IHost Host, FetchedProbe Probe)> StartSubscriberAsync()
    {
        var probe = new FetchedProbe();
        var serviceName = $"outbox-probe-{Guid.NewGuid():N}";
        var queue = nameof(RawDocumentFetched);
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = serviceName;
                opts.Discovery.DisableConventionalDiscovery().IncludeType<FetchedProbeHandler>();
                opts.Services.AddSingleton(probe);
                opts.UsePlatformRabbitMq(rabbit.ConnectionString!).AutoProvision()
                    .BindPlatformQueue<RawDocumentFetched>(serviceName, queue);
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

    // 端点（`RetryConversionJobEndpoint`）と同じ形の確定。
    private static async Task<RawDocumentFetched?> RetryAsync(IServiceProvider scoped, Guid id)
    {
        var store = scoped.GetRequiredService<IConversionJobStore>();
        var retry = scoped.GetRequiredService<IConversionRetryPublisher>();
        RawDocumentFetched? ev = null;
        await retry.CommitAsync(async c =>
        {
            ev = await store.PrepareRetryAsync(id, ct: c);
            if (ev is not null)
                await retry.PublishRetryAsync(ev, c);
        }, Ct);
        return ev;
    }

    // C1: 確定すれば、ジョブは queued になりイベントが出る。
    [Fact]
    public async Task 確定するとジョブはqueuedになり再変換のイベントが出る()
    {
        var cs = await CreateDatabaseAsync();
        var id = Guid.NewGuid();
        await SeedFailedAsync(cs, id);
        var (subscriber, probe) = await StartSubscriberAsync();
        using var _ = subscriber;
        using var publisher = await StartPublisherAsync(cs);

        await using (var scope = publisher.Services.CreateAsyncScope())
            (await RetryAsync(scope.ServiceProvider, id)).Should().NotBeNull();

        (await probe.Received(id).WaitAsync(Delivery, Ct)).Should().Be(id);
        (await StatusAsync(cs, id)).Should().Be(ConversionJobStatus.Queued, "DB の遷移も同じ確定でコミットされている");
        await WaitOutboxDrainedAsync(cs, Delivery);
        probe.CountFor(id).Should().Be(1);
    }

    // C2: 確定が失敗すれば、ジョブは failed のままでイベントも Outbox の行も出ない。
    //
    // 失敗は「保存と発行を積んだ後の関数内の例外」で起こす（端点の関数の続きで何かが落ちた場合と同じ）。否定（来ないこと）を
    // 時間待ちで測らないため、同じスコープで**後から**別のジョブの再変換を 1 件確定させ、それが届いた時点で先の 1 件が
    // 届いていないことを確かめる（同じ exchange・同じキューを同じ順で通る）。
    // 🔴 失敗時の巻き戻し（`ClearAllAsync`）を外すと、**確定しなかった 1 件目のイベントが 2 件目の確定と一緒に送られる**。
    [Fact]
    public async Task DBの確定が失敗すれば再変換のイベントは出ずジョブはfailedのまま()
    {
        var cs = await CreateDatabaseAsync();
        var failed = Guid.NewGuid();
        var sentinel = Guid.NewGuid();
        await SeedFailedAsync(cs, failed);
        await SeedFailedAsync(cs, sentinel);
        var (subscriber, probe) = await StartSubscriberAsync();
        using var _ = subscriber;
        using var publisher = await StartPublisherAsync(cs);

        await using (var scope = publisher.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IConversionJobStore>();
            var retry = scope.ServiceProvider.GetRequiredService<IConversionRetryPublisher>();
            var db = scope.ServiceProvider.GetRequiredService<ConversionJobDbContext>();

            var act = () => retry.CommitAsync(async c =>
            {
                var ev = await store.PrepareRetryAsync(failed, ct: c);
                await retry.PublishRetryAsync(ev!, c);
                throw new InvalidOperationException("確定の前の失敗（試験の注入）");
            }, Ct);
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*試験の注入*");
            (await OutboxRowsAsync(cs)).Should().Be(0, "確定が失敗すれば Outbox の行も巻き戻る");
            (await StatusAsync(cs, failed)).Should().Be(ConversionJobStatus.Failed, "queued への遷移も巻き戻る（再変換をやり直せる）");

            // 失敗した変更を捨てて、同じスコープ（同じ Outbox）で次の再変換を確定する。
            db.ChangeTracker.Clear();
            (await RetryAsync(scope.ServiceProvider, sentinel)).Should().NotBeNull();
        }

        (await probe.Received(sentinel).WaitAsync(Delivery, Ct)).Should().Be(sentinel);
        await WaitOutboxDrainedAsync(cs, Delivery);
        probe.CountFor(failed).Should().Be(0, "確定しなかった再変換のイベントは送られない（後の確定にも混ざらない）");
    }

    // C3: コミットの後・送出の前に止まっても Outbox の表に残り、再起動したホストが送る。
    // 発行口が内部で行う 2 段（DB のコミット → 送出）の 1 段目だけを実行して「送出の前のプロセス終了」を作る。
    // 🔴 耐久送出（`UseDurableOutboxOnAllSendingEndpoints`）を外すと、発行は表へ書かれず（行数 0）ここで赤になる。
    [Fact]
    public async Task 確定の後に送出が失われても再起動後に再送される()
    {
        var cs = await CreateDatabaseAsync();
        var id = Guid.NewGuid();
        await SeedFailedAsync(cs, id);
        var (subscriber, probe) = await StartSubscriberAsync();
        using var _ = subscriber;

        using (var crashed = await StartPublisherAsync(cs))
        {
            await using (var scope = crashed.Services.CreateAsyncScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IConversionJobStore>();
                var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<ConversionJobDbContext>>();
                await outbox.DbContext.Database.BeginTransactionAsync(Ct);
                var ev = await store.PrepareRetryAsync(id, ct: Ct);
                await outbox.PublishAsync(ev!);

                // コミットだけを行い、送出（FlushOutgoingMessagesAsync）へ進まない。
                await outbox.DbContext.Database.CommitTransactionAsync(Ct);
            }

            (await StatusAsync(cs, id)).Should().Be(ConversionJobStatus.Queued, "DB の遷移はコミット済み");
            (await OutboxRowsAsync(cs)).Should().Be(1, "送られていないイベントは Outbox の表（台帳）に残る");
            await crashed.StopAsync(Ct);
        }

        using var restarted = await StartPublisherAsync(cs);
        (await probe.Received(id).WaitAsync(Recovery, Ct)).Should().Be(id, "再起動したホストが Outbox の表から送る");
        await WaitOutboxDrainedAsync(cs, Delivery);
    }

    // C4: 外側のトランザクションが開いているときは確定しない（例外）。外側を勝手にコミットしない。
    [Fact]
    public async Task 外側のトランザクションの中では確定せず例外にする()
    {
        var cs = await CreateDatabaseAsync();
        var id = Guid.NewGuid();
        await SeedFailedAsync(cs, id);
        using var publisher = await StartPublisherAsync(cs);

        await using (var scope = publisher.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConversionJobDbContext>();
            await using var outer = await db.Database.BeginTransactionAsync(Ct);

            var act = () => RetryAsync(scope.ServiceProvider, id);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*トランザクション*");
            db.Database.CurrentTransaction.Should().BeSameAs(outer, "外側のトランザクションは開いたまま（確定も破棄もしていない）");
            await outer.RollbackAsync(Ct);
        }

        (await StatusAsync(cs, id)).Should().Be(ConversionJobStatus.Failed);
        (await OutboxRowsAsync(cs)).Should().Be(0);
    }

    public sealed class FetchedProbe
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

    public sealed class FetchedProbeHandler
    {
        public void Handle(RawDocumentFetched message, FetchedProbe probe) => probe.Record(message.FetchId);
    }
}
