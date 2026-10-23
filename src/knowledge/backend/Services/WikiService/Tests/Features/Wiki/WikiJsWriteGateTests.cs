using AwesomeAssertions;
using Knowledge.Contracts.Events;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Infrastructure.Foundation.Messaging;
using WikiService.Domain.Ports;
using WikiService.Features.Wiki;
using WikiService.Features.Wiki.RemoveDeleted;
using WikiService.Features.Wiki.SyncDocument;
using WikiService.Infrastructure.Persistence;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace WikiService.Tests.Features.Wiki;

// FR-13 テスト仕様 (#1971) —— UC-07, ADR-0011, ADR-0027, IADR-0557 決定 5, IADR-0563: Wiki.js へ同時に書き込む数の上限。
//
// Wiki.js 2.5 の pages.create / pages.delete は木の再構築（rebuild-tree）を truncate → insert で走らせ、並ぶと pageTree の主キーで衝突する。
// 同期と撤去は別のリスナーで並んで動くので、**両方の受け口が同じ門を通り、Wiki.js への呼び出しが上限を超えて重ならない**ことを測る。
[Trait("TestKind", "Unit")]
public class WikiJsWriteGateTests
{
    private static IConfiguration Config(string? value) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [WikiJsWriteGate.MaxParallelWritesKey] = value })
        .Build();

    // IADR-0563 決定 1: 既定は 1（直列）。
    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("4", 4)]
    public void 上限は既定1で構成から1から4を読む(string? configured, int expected)
    {
        var max = WikiJsWriteGate.ReadMaxParallelWrites(Config(configured));
        using var gate = new WikiJsWriteGate(max);

        max.Should().Be(expected);
        gate.MaxParallelWrites.Should().Be(expected);
    }

    // 範囲の外・整数でない値は起動で止める（黙って丸めない）。
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("5")]
    [InlineData("two")]
    public void 範囲の外か整数でない上限は起動を止める(string configured)
    {
        var act = () => WikiJsWriteGate.ReadMaxParallelWrites(Config(configured));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{WikiJsWriteGate.MaxParallelWritesKey}*");
    }

    // IADR-0563 決定 2: 同期（作成・アーカイブ・撤去の 3 つの口）と撤去の受け口を同時に流しても、Wiki.js への呼び出しは上限を超えて重ならない。
    // 対照として上限 2 では 2 まで重なることも測る（重なりを測れない器で「1」を見ても意味が無い）。
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task 同期と撤去の受け口はWikiJsへの呼び出しを上限より重ねない(int limit)
    {
        using var gate = new WikiJsWriteGate(limit);
        var wiki = new OverlapProbe();
        var timeouts = WikiSyncTimeouts.Default;
        var ct = TestContext.Current.CancellationToken;

        var runs = new List<Task>();
        for (var i = 0; i < 3; i++)
        {
            runs.Add(Sync(wiki, gate, timeouts, "published", ct));
            runs.Add(Sync(wiki, gate, timeouts, "archived", ct));
            runs.Add(Delete(wiki, gate, timeouts, ct));
        }
        await Task.WhenAll(runs);

        wiki.Calls.Should().Be(9);
        wiki.MaxOverlap.Should().Be(limit);
    }

    // 門の待ちが期限を使い切ったら、受け口の取り消しではなく呼び出し先 `wiki-js-gate` の時間切れとして投げる（再試行へ委ねる）。
    // 前の書き込みが終われば枠は返り、次の呼び出しは通る。
    [Fact]
    public async Task 門の待ちが期限を超えたら門の時間切れとして投げ枠は前の書き込みの後に返る()
    {
        using var gate = new WikiJsWriteGate(1);
        var calls = ConsumerTimeoutsForTests.Calls();
        var ct = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource();

        var holder = gate.RunAsync(calls, "wiki-sync", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), _ => release.Task, ct);
        var waiter = () => gate.RunAsync(calls, "wiki-delete", TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(30), _ => Task.CompletedTask, ct);

        var thrown = (await waiter.Should().ThrowAsync<ConsumerTimeoutException>()).Which;
        thrown.Target.Should().Be(WikiSyncTimeouts.WikiJsGateTarget);
        thrown.Step.Should().Be("wiki-delete");

        release.SetResult();
        await holder;
        var ran = false;
        await gate.RunAsync(calls, "wiki-delete", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), _ => { ran = true; return Task.CompletedTask; }, ct);
        ran.Should().BeTrue();
    }

    // 呼び出しが失敗（例外・呼び出しの時間切れ）しても枠は返る（返らなければ以後の同期が全部門で止まる）。
    [Fact]
    public async Task 呼び出しが失敗しても枠は返る()
    {
        using var gate = new WikiJsWriteGate(1);
        var calls = ConsumerTimeoutsForTests.Calls();
        var ct = TestContext.Current.CancellationToken;

        var failing = () => gate.RunAsync(calls, "wiki-sync", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            _ => throw new WikiService.Infrastructure.ExternalServices.WikiJsSyncException("boom"), ct);
        await failing.Should().ThrowAsync<WikiService.Infrastructure.ExternalServices.WikiJsSyncException>();

        var hung = () => gate.RunAsync(calls, "wiki-sync", TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100),
            t => Task.Delay(Timeout.Infinite, t), ct);
        (await hung.Should().ThrowAsync<ConsumerTimeoutException>()).Which.Target.Should().Be(WikiSyncTimeouts.WikiJsTarget);

        var ran = false;
        await gate.RunAsync(calls, "wiki-sync", TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500), _ => { ran = true; return Task.CompletedTask; }, ct);
        ran.Should().BeTrue();
    }

    // FR-13, NFR-05, IADR-0599 決定 1・2 (#2100): 門はプロセスの中の枠を取った後にレプリカをまたぐ枠（上限の数を渡す）を取り、
    // Wiki.js の呼び出しの後に返す。呼び出しが失敗しても返す。
    [Fact]
    public async Task 門はレプリカをまたぐ枠を取ってからWikiJsを呼び呼び出しの後に返す()
    {
        var replica = new RecordingReplicaLock();
        using var gate = new WikiJsWriteGate(2, replica);
        var calls = ConsumerTimeoutsForTests.Calls();
        var ct = TestContext.Current.CancellationToken;

        var heldDuringCall = false;
        await gate.RunAsync(calls, "wiki-sync", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            _ => { heldDuringCall = replica.Held == 1; return Task.CompletedTask; }, ct);
        var failing = () => gate.RunAsync(calls, "wiki-sync", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            _ => throw new WikiService.Infrastructure.ExternalServices.WikiJsSyncException("boom"), ct);
        await failing.Should().ThrowAsync<WikiService.Infrastructure.ExternalServices.WikiJsSyncException>();

        heldDuringCall.Should().BeTrue("Wiki.js を呼ぶ間はレプリカをまたぐ枠を持っている");
        replica.RequestedSlots.Should().Equal(2, 2);
        replica.Held.Should().Be(0, "呼び出しの成否に関わらず枠は返る");
    }

    // レプリカをまたぐ枠を取れない（DB へ届かない）ときは Wiki.js を呼ばない（枠なしで強行しない＝fail-closed）。
    // プロセスの中の枠は返す（返さなければ以後の同期が全部門で止まる）。
    [Fact]
    public async Task レプリカをまたぐ枠を取れなければWikiJsを呼ばずプロセスの中の枠は返す()
    {
        var replica = new RecordingReplicaLock { FailNext = true };
        using var gate = new WikiJsWriteGate(1, replica);
        var calls = ConsumerTimeoutsForTests.Calls();
        var ct = TestContext.Current.CancellationToken;

        var called = false;
        var act = () => gate.RunAsync(calls, "wiki-sync", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            _ => { called = true; return Task.CompletedTask; }, ct);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*DB*");
        called.Should().BeFalse();

        await gate.RunAsync(calls, "wiki-sync", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
            _ => { called = true; return Task.CompletedTask; }, ct);
        called.Should().BeTrue("プロセスの中の枠は返っている（上限 1 でも次が通る）");
    }

    // レプリカをまたぐ枠の待ちも門の待ちの期限に入る。超えたら `wiki-js-gate` の時間切れ（再試行へ委ねる）。
    [Fact]
    public async Task レプリカをまたぐ枠の待ちが期限を超えたら門の時間切れとして投げる()
    {
        var replica = new RecordingReplicaLock { HangNext = true };
        using var gate = new WikiJsWriteGate(1, replica);
        var calls = ConsumerTimeoutsForTests.Calls();

        var act = () => gate.RunAsync(calls, "wiki-delete", TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(30),
            _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<ConsumerTimeoutException>()).Which.Target.Should().Be(WikiSyncTimeouts.WikiJsGateTarget);
        replica.Held.Should().Be(0);
    }

    private sealed class RecordingReplicaLock : IWikiJsReplicaWriteLock
    {
        private int _held;
        public List<int> RequestedSlots { get; } = [];
        public int Held => Volatile.Read(ref _held);
        public bool FailNext { get; set; }
        public bool HangNext { get; set; }

        public async Task<IAsyncDisposable> AcquireAsync(int slots, CancellationToken ct)
        {
            RequestedSlots.Add(slots);
            if (FailNext) { FailNext = false; throw new InvalidOperationException("DB へ届かない"); }
            if (HangNext) { HangNext = false; await Task.Delay(Timeout.Infinite, ct); }
            Interlocked.Increment(ref _held);
            return new Lease(this);
        }

        private sealed class Lease(RecordingReplicaLock owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { Interlocked.Decrement(ref owner._held); return ValueTask.CompletedTask; }
        }
    }

    private static WikiDbContext NewDb() => new(
        new DbContextOptionsBuilder<WikiDbContext>().UseInMemoryDatabase($"gate_{Guid.NewGuid():N}").Options);

    private static async Task Sync(OverlapProbe wiki, WikiJsWriteGate gate, WikiSyncTimeouts timeouts, string status, CancellationToken ct)
    {
        await using var db = NewDb();
        var ev = new DocumentUpdated(Guid.NewGuid(), "門の試験", status, "storage://b/g.md",
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, ["ops"], DateTimeOffset.UtcNow);
        await new DocumentSyncConsumer(db, wiki, wiki, ConsumerTimeoutsForTests.Calls(), timeouts, gate,
                TestMeterFactory.NewSkippedMetrics(), NullLogger<DocumentSyncConsumer>.Instance)
            .Handle(ev, ct);
    }

    private static async Task Delete(OverlapProbe wiki, WikiJsWriteGate gate, WikiSyncTimeouts timeouts, CancellationToken ct)
    {
        await using var db = NewDb();
        await new DocumentDeletedConsumer(db, wiki, wiki, ConsumerTimeoutsForTests.Calls(), timeouts, gate,
                NullLogger<DocumentDeletedConsumer>.Instance)
            .Handle(new DocumentDeleted(Guid.NewGuid(), DateTimeOffset.UtcNow), ct);
    }

    // Wiki.js への呼び出しの重なりを数える器。1 回の呼び出しは 100 ms かかる（重なりが起きる余地を作る）。
    private sealed class OverlapProbe : IWikiJsClient, IWikiContentReader
    {
        private int _inFlight;
        private int _maxOverlap;
        private int _calls;

        public int MaxOverlap => Volatile.Read(ref _maxOverlap);
        public int Calls => Volatile.Read(ref _calls);

        private async Task WriteAsync(CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _inFlight);
            Interlocked.Increment(ref _calls);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxOverlap)))
                Interlocked.CompareExchange(ref _maxOverlap, now, seen);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public Task<string> ReadAsync(string? markdownUri, string title, CancellationToken ct = default) => Task.FromResult("# 本文");
        public Task UpsertPageAsync(WikiJsPage page, CancellationToken ct = default) => WriteAsync(ct);
        public Task ArchivePageAsync(string path, CancellationToken ct = default) => WriteAsync(ct);
        public Task DeletePageAsync(string path, CancellationToken ct = default) => WriteAsync(ct);
        public Task<string?> GetRenderedContentAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

// 本番の Program.cs の配線: 門を DI に置き、wiki-service の 2 つのリスナーの数を門の上限にそろえる。
// 🔴 RabbitMQ のリスナーは Wolverine の既定で Inline（1 通の処理を待ってから次を受け取る）なので、並びを決めるのはリスナーの数である。
// その前提（Mode=Inline）も固定する —— Buffered へ変わると、リスナーの数ではなく MaxDegreeOfParallelism（既定 5 以上）が並びを決める。
[Trait("TestKind", "Integration")]
public class WikiJsWriteGateWiringTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    // リスナーの構成（ListenerCount 等）は Wolverine が端点を組み立てる（Compile）ときに当たる。試験の器は外部の輸送を止めて
    // いて端点を組み立てないので、ここで組み立ててから読む（本番は起動時に組み立てる）。
    private static Dictionary<string, (EndpointMode Mode, int Listeners)> WikiListeners(IServiceProvider services)
    {
        var runtime = services.GetRequiredService<IWolverineRuntime>();
        return runtime.Options.Transports
            .SelectMany(t => t.Endpoints())
            .Where(e => e.IsListener && e.EndpointName.StartsWith("wiki-service.", StringComparison.Ordinal))
            .Select(e => { e.Compile(runtime); return e; })
            .ToDictionary(e => e.EndpointName, e => (e.Mode, e.ListenerCount));
    }

    [Fact]
    public void 既定では門の上限は1で両リスナーはInlineの1本()
    {
        factory.Services.GetRequiredService<WikiJsWriteGate>().MaxParallelWrites.Should().Be(1);
        // IADR-0599 決定 2: 試験の器は DB を InMemory へ差し替えるので、レプリカをまたぐ枠はプロセスの中の枠へ縮退する
        // （リレーショナルなら PostgreSQL の advisory lock。実 PostgreSQL での振る舞いは Knowledge.IntegrationTests の WikiJsReplicaWriteLockTests）。
        factory.Services.GetRequiredService<IWikiJsReplicaWriteLock>().Should().BeSameAs(
            WikiService.Infrastructure.Persistence.ProcessLocalWikiJsReplicaWriteLock.Instance);

        WikiListeners(factory.Services).Should().BeEquivalentTo(new Dictionary<string, (EndpointMode, int)>
        {
            ["wiki-service.DocumentUpdated"] = (EndpointMode.Inline, 1),
            ["wiki-service.DocumentDeleted"] = (EndpointMode.Inline, 1),
        });
    }

    [Fact]
    public void 構成した上限が門と両リスナーの数へ同じ値で入る()
    {
        using var configured = factory.WithWebHostBuilder(b => b.UseSetting(WikiJsWriteGate.MaxParallelWritesKey, "3"));

        configured.Services.GetRequiredService<WikiJsWriteGate>().MaxParallelWrites.Should().Be(3);
        WikiListeners(configured.Services).Values.Should().Equal((EndpointMode.Inline, 3), (EndpointMode.Inline, 3));
    }
}
