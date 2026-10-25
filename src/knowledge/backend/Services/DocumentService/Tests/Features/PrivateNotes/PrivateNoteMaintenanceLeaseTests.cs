using AwesomeAssertions;
using DocumentService.Domain.Ports;
using DocumentService.Features.PrivateNotes.Maintenance;
using DocumentService.Infrastructure.Persistence;
using DocumentService.Tests.Infrastructure.ExternalServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DocumentService.Tests.Features.PrivateNotes;

// FR-19, FR-22, ADR-0096 決定 1・2, IADR-0599 決定 5 (#2100): 個人資料の定期処理は、リースを取れたレプリカだけが周期の本体を回す。
// 実 PostgreSQL の 2 ホスト相当は Knowledge.IntegrationTests の `PrivateNoteMaintenanceSingleRunTests` が測る。ここはゲートの形だけを測る。
[Trait("TestKind", "Unit")]
public class PrivateNoteMaintenanceLeaseTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private static IServiceScopeFactory Scopes() =>
        new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    [Fact]
    public async Task リースを取れなければ周期の本体を回さない()
    {
        var ran = 0;
        var worker = new PrivateNoteMaintenanceHostedService(Scopes(), new DenyingCoordinator(),
            new RecordingLogger<PrivateNoteMaintenanceHostedService>())
        { CycleBody = (_, _) => { ran++; return Task.CompletedTask; } };

        (await worker.TryRunCycleAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
        ran.Should().Be(0);
    }

    // リースは本体の間ずっと持ち、本体の成否に関わらず本体の後に返す。
    [Fact]
    public async Task リースを本体の間持ち本体が失敗しても返す()
    {
        var coordinator = new GrantingCoordinator();
        var heldDuringBody = new List<bool>();
        var fail = false;
        var worker = new PrivateNoteMaintenanceHostedService(Scopes(), coordinator,
            new RecordingLogger<PrivateNoteMaintenanceHostedService>())
        {
            CycleBody = (_, _) =>
            {
                heldDuringBody.Add(coordinator.Held == 1);
                return fail ? throw new InvalidOperationException("本体の失敗") : Task.CompletedTask;
            },
        };

        (await worker.TryRunCycleAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
        fail = true;
        var act = () => worker.TryRunCycleAsync(TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();

        heldDuringBody.Should().Equal(true, true);
        coordinator.Held.Should().Be(0);
        coordinator.Acquired.Should().Be(2);
    }

    // 本番の配線: 定期処理はリースのコーディネータを受けて登録される。試験の器（InMemory）では常時取得へ縮退する
    // （リレーショナルなら PostgreSQL の advisory lock。GraphService と同じ選び方）。
    [Fact]
    public void 本番の配線はコーディネータを登録しInMemoryでは常時取得へ縮退する()
    {
        factory.Services.GetRequiredService<IPrivateNoteMaintenanceLeaseCoordinator>()
            .Should().BeSameAs(NoOpPrivateNoteMaintenanceLeaseCoordinator.Instance);
        factory.Services.GetServices<IHostedService>().OfType<PrivateNoteMaintenanceHostedService>().Should().ContainSingle();
    }

    private sealed class DenyingCoordinator : IPrivateNoteMaintenanceLeaseCoordinator
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct) => Task.FromResult<IAsyncDisposable?>(null);
    }

    private sealed class GrantingCoordinator : IPrivateNoteMaintenanceLeaseCoordinator
    {
        private int _held;
        public int Held => Volatile.Read(ref _held);
        public int Acquired { get; private set; }

        public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct)
        {
            Acquired++;
            Interlocked.Increment(ref _held);
            return Task.FromResult<IAsyncDisposable?>(new Lease(this));
        }

        private sealed class Lease(GrantingCoordinator owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { Interlocked.Decrement(ref owner._held); return ValueTask.CompletedTask; }
        }
    }
}
