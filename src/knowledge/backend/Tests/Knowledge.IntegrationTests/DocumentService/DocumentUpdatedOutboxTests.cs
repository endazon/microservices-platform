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
using global::DocumentService.Features.Documents;
using global::DocumentService.Infrastructure.Messaging;
using global::DocumentService.Infrastructure.Persistence;

namespace Knowledge.IntegrationTests.DocumentService;

// 🔴 FR-06, ADR-0027 §決定「イベント発行の原子性は Wolverine の EF Core + PostgreSQL Transactional Outbox を標準とする」,
// IADR-0608 (#2168): **更新の確定と `DocumentUpdated` が同じトランザクションで確定する**ことを実 PostgreSQL ＋ 実 RabbitMQ で測る。
// 段 1 の `DocumentDeletedOutboxTests`（IADR-0604）と同型である。
//
// 被験体は本番の部品そのものである: 共通ヘルパ（`UsePlatformMessagingDefaults` ＋ `UsePlatformTransactionalOutbox`）、
// 本番の経路宣言（`RoutePlatformEvent<DocumentUpdated>`）、本番の発行口（`WolverineDocumentUpdatedPublisher`）、
// 本番の門の確定込みの入口（`DocumentEndpoints.SaveChangesAndPublishUpdatedIfIndexableAsync`）、Migration 済みの `DocumentDbContext`。
//
// 4 つの性質（作業仕様書 20261023_2168 の B1〜B4）:
//   B1 確定すれば、台帳の変更とイベントの両方が出る。送られた後は Outbox の表に行が残らない。
//   B2 確定が失敗すれば、イベントも Outbox の行も出ない（同じスコープの後続の確定にも混ざらない）。
//   B3 コミットの後・送出の前にプロセスが止まっても Outbox の表に残り、再起動したホストが送る。
//   B4 イベントの中身は保存の後の DB から組み立てる（同じ確定で足した共有がイベントの共有先に載る）。
//
// 試験ごとに専用の DB を作る（Outbox の表の行数を他の試験と混ぜずに数えるため）。
[Trait("Category", "Integration")]
public sealed class DocumentUpdatedOutboxTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 実処理（ブローカ越しの 1 通）の予算。購読開始は `ListenerReadiness` が別に待つ。
    private static readonly TimeSpan Delivery = TimeSpan.FromSeconds(30);

    // 再起動したホストの耐久エージェントが Outbox の表を拾うまでの予算（既定のポーリングは 5 秒）。
    private static readonly TimeSpan Recovery = TimeSpan.FromSeconds(60);

    private static readonly Dictionary<string, string> OrgAttributes = new() { ["confidentiality"] = "internal" };

    private async Task<string> CreateDatabaseAsync()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres, RequiredServices.Broker);
        var baseConnectionString = postgres.ConnectionString!;
        var name = $"docupdoutbox_{Guid.NewGuid():N}";
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
                services.AddScoped<IDocumentUpdatedPublisher, WolverineDocumentUpdatedPublisher>();
            })
            .UseWolverine(opts =>
            {
                opts.ServiceName = "document-updated-outbox-under-test";
                opts.Discovery.DisableConventionalDiscovery();
                opts.UsePlatformRabbitMq(rabbit.ConnectionString!).AutoProvision();
                opts.RoutePlatformEvent<DocumentUpdated>();
                opts.UsePlatformMessagingDefaults();
                opts.UsePlatformTransactionalOutbox(cs);
                opts.Durability.Mode = DurabilityMode.Solo;
            })
            .StartAsync(Ct);

    // 購読側のホスト。試験ごとに別のキューを `DocumentUpdated` の exchange へ束ねる。
    private async Task<(IHost Host, UpdatedProbe Probe)> StartSubscriberAsync()
    {
        var probe = new UpdatedProbe();
        var serviceName = $"updated-outbox-probe-{Guid.NewGuid():N}";
        var queue = nameof(DocumentUpdated);
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = serviceName;
                opts.Discovery.DisableConventionalDiscovery().IncludeType<UpdatedProbeHandler>();
                opts.Services.AddSingleton(probe);
                opts.UsePlatformRabbitMq(rabbit.ConnectionString!).AutoProvision()
                    .BindPlatformQueue<DocumentUpdated>(serviceName, queue);
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

    private static async Task<string?> TitleAsync(string cs, Guid id)
    {
        await using var db = new DocumentDbContext(new DbContextOptionsBuilder<DocumentDbContext>().UseNpgsql(cs).Options);
        return await db.Documents.Where(d => d.Id == id).Select(d => d.Title).SingleOrDefaultAsync(Ct);
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

    private static Document NewDocument(Guid id, string title) =>
        Document.CreateNormalized(id, title, $"storage://knowledge-normalized/{id:N}/document.md", new(OrgAttributes));

    // B1: 確定すれば、台帳の変更とイベントの両方が出る。
    [Fact]
    public async Task 確定すると台帳の変更と更新イベントの両方が出る()
    {
        var cs = await CreateDatabaseAsync();
        var (subscriber, probe) = await StartSubscriberAsync();
        using var _ = subscriber;
        using var publisher = await StartPublisherAsync(cs);
        var id = Guid.NewGuid();

        await using (var scope = publisher.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            var updated = scope.ServiceProvider.GetRequiredService<IDocumentUpdatedPublisher>();
            var doc = NewDocument(id, "確定する文書");
            db.Documents.Add(doc);
            await DocumentEndpoints.SaveChangesAndPublishUpdatedIfIndexableAsync(updated, db, doc, Ct);
        }

        (await probe.Received(id).WaitAsync(Delivery, Ct)).Title.Should().Be("確定する文書");
        (await TitleAsync(cs, id)).Should().Be("確定する文書", "DB の変更も同じ確定でコミットされている");
        await WaitOutboxDrainedAsync(cs, Delivery);
        probe.CountFor(id).Should().Be(1);
    }

    // B2: 確定が失敗すれば、イベントも Outbox の行も出ない。
    //
    // 失敗は「文書の主キーの重複」で起こす（実 PostgreSQL の制約違反）。否定（来ないこと）を時間待ちで測らないため、
    // 同じスコープで**後から**正しい更新を 1 件確定させ、それが届いた時点で先の 1 件が届いていないことを確かめる。
    // 🔴 失敗時の巻き戻しを外すと、確定しなかった 1 件目のイベントが 2 件目の確定と一緒に送られる（段 1 と同じ壊れ方）。
    [Fact]
    public async Task DBの確定が失敗すれば更新イベントは出ない()
    {
        var cs = await CreateDatabaseAsync();
        var failed = Guid.NewGuid();
        await using (var seed = new DocumentDbContext(new DbContextOptionsBuilder<DocumentDbContext>().UseNpgsql(cs).Options))
        {
            seed.Documents.Add(NewDocument(failed, "既に在る文書"));
            await seed.SaveChangesAsync(Ct);
        }
        var (subscriber, probe) = await StartSubscriberAsync();
        using var _ = subscriber;
        using var publisher = await StartPublisherAsync(cs);
        var sentinel = Guid.NewGuid();

        await using (var scope = publisher.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            var updated = scope.ServiceProvider.GetRequiredService<IDocumentUpdatedPublisher>();

            // 保存より前に発行を積む経路（`Republish` の形。保存で負けた変更と同じトランザクションに載る）も巻き戻ることを見るため、
            // 関数の中で先に積んでから保存する。
            var duplicate = NewDocument(failed, "重複する文書");
            db.Documents.Add(duplicate);
            var act = () => updated.CommitAsync(async c =>
            {
                await updated.PublishUpdatedAsync(failed, duplicate.Title, duplicate.Status, duplicate.MarkdownUri,
                    duplicate.Attributes, [], duplicate.UpdatedAt, ct: c);
                await db.SaveChangesAsync(c);
            }, Ct);
            await act.Should().ThrowAsync<DbUpdateException>("主キーの重複で確定が失敗する");
            (await OutboxRowsAsync(cs)).Should().Be(0, "確定が失敗すれば Outbox の行も巻き戻る");

            // 失敗した変更を捨てて、同じスコープ（同じ Outbox）で次の更新を確定する。
            db.ChangeTracker.Clear();
            var next = NewDocument(sentinel, "後続の文書");
            db.Documents.Add(next);
            await DocumentEndpoints.SaveChangesAndPublishUpdatedIfIndexableAsync(updated, db, next, Ct);
        }

        (await probe.Received(sentinel).WaitAsync(Delivery, Ct)).DocumentId.Should().Be(sentinel);
        await WaitOutboxDrainedAsync(cs, Delivery);
        probe.CountFor(failed).Should().Be(0, "確定しなかった更新のイベントは送られない（後の確定にも混ざらない）");
        (await TitleAsync(cs, failed)).Should().Be("既に在る文書");
    }

    // B3: コミットの後・送出の前に止まっても Outbox の表に残り、再起動したホストが送る。
    //
    // 「コミットの後・送出の前のプロセス終了」を、発行口が内部で行う段（トランザクションを開く → 保存 → 積む → コミット）を
    // 同じ部品（スコープの `IDbContextOutbox`）で行い、送出（FlushOutgoingMessagesAsync）の手前で止めて作る。
    // その後ホストを止め、同じ DB を向いた新しいホストを起こす —— 新しいホストの耐久エージェントが表から拾って送る。
    // 🔴 耐久送出（`UseDurableOutboxOnAllSendingEndpoints`）を外すと、発行は表へ書かれず（行数 0）ここで赤になる。
    [Fact]
    public async Task 確定の後に送出が失われても再起動後に再送される()
    {
        var cs = await CreateDatabaseAsync();
        var (subscriber, probe) = await StartSubscriberAsync();
        using var _ = subscriber;
        var id = Guid.NewGuid();

        using (var crashed = await StartPublisherAsync(cs))
        {
            await using (var scope = crashed.Services.CreateAsyncScope())
            {
                var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<DocumentDbContext>>();
                var doc = NewDocument(id, "送出の前に止まる文書");
                outbox.DbContext.Documents.Add(doc);
                await outbox.DbContext.Database.BeginTransactionAsync(Ct);
                await outbox.DbContext.SaveChangesAsync(Ct);
                await outbox.PublishAsync(new DocumentUpdated(doc.Id, doc.Title, doc.Status, doc.MarkdownUri,
                    doc.Attributes, [], doc.UpdatedAt));
                await outbox.DbContext.Database.CommitTransactionAsync(Ct);
            }

            (await TitleAsync(cs, id)).Should().Be("送出の前に止まる文書", "DB の変更はコミット済み");
            (await OutboxRowsAsync(cs)).Should().Be(1, "送られていないイベントは Outbox の表（台帳）に残る");
            await crashed.StopAsync(Ct);
        }

        using var restarted = await StartPublisherAsync(cs);
        (await probe.Received(id).WaitAsync(Recovery, Ct)).DocumentId.Should().Be(id, "再起動したホストが Outbox の表から送る");
        await WaitOutboxDrainedAsync(cs, Delivery);
    }

    // B4: イベントの中身は保存の後の DB から組み立てる。
    //
    // 共有の付与（`GrantShare` の形）: 共有の行の追加と、その文書の再発行を 1 回の確定に載せる。共有先は門が共有台帳を
    // 引いて解決する（`ResolveSharedWithAsync`）。🔴 段 1 の「積む → 確定」の順（保存の前に積む）へ戻すと、台帳にまだ行が無く
    // 共有先が空のイベントが出る —— この試験が赤になる（IADR-0608 決定 1）。
    [Fact]
    public async Task 保存の後のDBを読んで発行する()
    {
        var cs = await CreateDatabaseAsync();
        var id = Guid.NewGuid();
        await using (var seed = new DocumentDbContext(new DbContextOptionsBuilder<DocumentDbContext>().UseNpgsql(cs).Options))
        {
            seed.Documents.Add(NewDocument(id, "共有する文書"));
            await seed.SaveChangesAsync(Ct);
        }
        var (subscriber, probe) = await StartSubscriberAsync();
        using var _ = subscriber;
        using var publisher = await StartPublisherAsync(cs);

        await using (var scope = publisher.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            var updated = scope.ServiceProvider.GetRequiredService<IDocumentUpdatedPublisher>();
            var doc = await db.Documents.SingleAsync(d => d.Id == id, Ct);
            db.DocumentShares.Add(DocumentShare.Create(id, ShareSubjectType.User, "alice", "owner"));
            await DocumentEndpoints.SaveChangesAndPublishUpdatedIfIndexableAsync(updated, db, doc, Ct);
        }

        (await probe.Received(id).WaitAsync(Delivery, Ct)).SharedWith.Should().BeEquivalentTo(["alice"],
            "同じ確定で足した共有がイベントの共有先に載る（保存の後の台帳を読んでいる）");
        await WaitOutboxDrainedAsync(cs, Delivery);
    }

    // IADR-0608 決定 2: 外側のトランザクションが開いているときは確定しない（例外）。外側を勝手にコミットしない。
    // 🔴 「開いていれば新しく開かず、そのまま確定する」形へ戻す変異で、外側の変更がコミットされて赤になる。
    [Fact]
    public async Task 外側のトランザクションの中では確定せず例外にする()
    {
        var cs = await CreateDatabaseAsync();
        using var publisher = await StartPublisherAsync(cs);
        var id = Guid.NewGuid();

        await using (var scope = publisher.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            var updated = scope.ServiceProvider.GetRequiredService<IDocumentUpdatedPublisher>();
            await using var outer = await db.Database.BeginTransactionAsync(Ct);
            var doc = NewDocument(id, "外側のトランザクションの文書");
            db.Documents.Add(doc);

            var act = () => DocumentEndpoints.SaveChangesAndPublishUpdatedIfIndexableAsync(updated, db, doc, Ct);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*トランザクション*");
            db.Database.CurrentTransaction.Should().BeSameAs(outer, "外側のトランザクションは開いたまま（確定も破棄もしていない）");
            await outer.RollbackAsync(Ct);
        }

        (await TitleAsync(cs, id)).Should().BeNull("外側を巻き戻したので、文書は確定していない");
        (await OutboxRowsAsync(cs)).Should().Be(0);
    }

    public sealed class UpdatedProbe
    {
        private readonly ConcurrentDictionary<Guid, int> _counts = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<DocumentUpdated>> _waiters = new();

        public void Record(DocumentUpdated message)
        {
            _counts.AddOrUpdate(message.DocumentId, 1, (_, n) => n + 1);
            Waiter(message.DocumentId).TrySetResult(message);
        }

        public int CountFor(Guid id) => _counts.TryGetValue(id, out var n) ? n : 0;

        public Task<DocumentUpdated> Received(Guid id) => Waiter(id).Task;

        private TaskCompletionSource<DocumentUpdated> Waiter(Guid id) =>
            _waiters.GetOrAdd(id, _ => new TaskCompletionSource<DocumentUpdated>(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    public sealed class UpdatedProbeHandler
    {
        public void Handle(DocumentUpdated message, UpdatedProbe probe) => probe.Record(message);
    }
}
