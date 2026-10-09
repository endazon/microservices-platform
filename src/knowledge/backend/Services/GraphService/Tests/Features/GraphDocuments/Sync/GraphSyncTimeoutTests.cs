using System.Diagnostics;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using GraphService.Common.Observability;
using GraphService.Domain.Ports;
using GraphService.Features.GraphDocuments.Sync;
using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Infrastructure.Foundation.Messaging;
using Wolverine.Runtime.Handlers;

namespace GraphService.Tests.Features.GraphDocuments.Sync;

// FR-17 テスト仕様 (#1640) —— ADR-0027, ADR-0033: グラフ同期の受け口の本文の取得の期限。
//
// 🔴 **縮めた受け口の ct の下で測る。** 受け口の ct は Wolverine の実行期限（本受け口は既定 60 秒）を含む。ここでは 30 秒の CTS で模し（呼び出しごとの 1 秒と十分に離し、負荷下の揺らぎで比が崩れないようにする）、
// 本文の取得の期限（1 秒）が**先に**立って時間切れ（`ConsumerTimeoutException`）になること —— 受け口の ct は立っていないこと —— を見る。
// 期限を外す変異では、止まったストレージは受け口の ct で取り消し（`OperationCanceledException`）として落ち、赤になる。
[Trait("TestKind", "Unit")]
public class GraphSyncTimeoutTests
{
    private static readonly Guid Doc = Guid.Parse("cccccccc-0000-0000-0000-0000000000c1");
    private static readonly TimeSpan HandlerBudget = TimeSpan.FromSeconds(30);

    private static DbContextOptions<GraphDbContext> Options(string name) =>
        new DbContextOptionsBuilder<GraphDbContext>().UseInMemoryDatabase(name).Options;

    private static GraphDocumentSyncConsumer Build(GraphDbContext db, TimeSpan contentRead) => new(
        db, TimeProvider.System, new HangingReader(),
        new LinkEdgeSynchronizer(db, new EdgeTypeFallbackMetrics(new MeterFactory()),
            NullLogger<LinkEdgeSynchronizer>.Instance),
        new TermProfileSynchronizer(db), TagEdgesForTests.Synchronizer(db),
        ConsumerTimeoutsForTests.Calls(), new GraphSyncTimeouts(contentRead),
        NullLogger<GraphDocumentSyncConsumer>.Instance);

    // 指紋が変わる（＝本文を読みに行く）更新イベント。
    private static DocumentUpdated Event() => new(Doc, "時間切れの試験", "published", "storage://b/c.md",
        new Dictionary<string, string> { ["confidentiality"] = "internal" }, ["ops"],
        DateTimeOffset.UtcNow, "fp-1");

    // 止まったストレージは受け口の ct が立つより前に時間切れとして投げ、**本文なし（null）へ畳まない** ——
    // 同期は保存されず（辺も属性も書かない）、再試行で本文の変化が取り直される。
    [Fact]
    public async Task 止まった本文の取得は受け口の期限より前に時間切れとして投げ保存しない()
    {
        var name = $"gst_{Guid.NewGuid():N}";
        await using (var db = new GraphDbContext(Options(name)))
        {
            await EdgeTypeSeed.EnsureSeededAsync(db, TestContext.Current.CancellationToken);
            var consumer = Build(db, TimeSpan.FromSeconds(1));
            using var handler = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            handler.CancelAfter(HandlerBudget);
            var started = Stopwatch.GetTimestamp();

            var act = () => consumer.Handle(Event(), handler.Token);

            var thrown = (await act.Should().ThrowAsync<ConsumerTimeoutException>()).Which;
            handler.IsCancellationRequested.Should().BeFalse("本文の取得の期限が受け口の ct より先に立つ");
            Stopwatch.GetElapsedTime(started).Should().BeLessThan(HandlerBudget);
            thrown.Step.Should().Be(GraphDocumentSyncConsumer.StepName);
            thrown.Target.Should().Be(GraphSyncTimeouts.ContentTarget);
        }

        await using var fresh = new GraphDbContext(Options(name));
        (await fresh.Documents.AnyAsync(d => d.DocumentId == Doc, TestContext.Current.CancellationToken))
            .Should().BeFalse("時間切れの同期は保存しない（再試行で取り直す）");
    }

    // 対照: 呼び出し元の取り消しは時間切れに化けず、取り消しのまま外へ出る。
    [Fact]
    public async Task 呼び出し元の取り消しは取り消しのまま外へ出る()
    {
        await using var db = new GraphDbContext(Options($"gst_{Guid.NewGuid():N}"));
        await EdgeTypeSeed.EnsureSeededAsync(db, TestContext.Current.CancellationToken);
        var consumer = Build(db, TimeSpan.FromSeconds(30));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(TimeSpan.FromMilliseconds(200));

        var act = () => consumer.Handle(Event(), caller.Token);

        (await act.Should().ThrowAsync<OperationCanceledException>()).Which
            .Should().NotBeAssignableTo<TimeoutException>();
    }

    private static IConfiguration Config(string? seconds) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [GraphSyncTimeouts.ContentReadKey] = seconds })
        .Build();

    [Fact]
    public void 構成が無ければ既定の期限になる()
    {
        GraphSyncTimeouts.From(Config(null)).Should().Be(GraphSyncTimeouts.Default);
        GraphSyncTimeouts.Default.ContentRead.Should().Be(TimeSpan.FromSeconds(20));
    }

    // 本文の期限が Wolverine の既定 60 秒以上なら起動を止める（等しいときも止める）。
    [Theory]
    [InlineData("60")]
    [InlineData("100")]
    public void 本文の期限が既定の実行期限に収まらなければ起動を止める(string seconds)
    {
        var act = () => GraphSyncTimeouts.From(Config(seconds));

        act.Should().Throw<InvalidOperationException>().WithMessage("*graph-sync*");
    }

    private sealed class HangingReader : IGraphContentReader
    {
        public async Task<string?> ReadAsync(string? markdownUri, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }
    }

    private sealed class MeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) =>
            new($"{options.Name}.test-{Guid.NewGuid():N}", options.Version, options.Tags, scope: this);

        public void Dispose() { }
    }
}

// 本番の Program.cs の配線: 上限を DI に置き、実行期限は Wolverine の既定のまま（方針を入れない）。
[Trait("TestKind", "Integration")]
public class GraphSyncTimeoutWiringTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public void 本番の配線は本文の期限を既定値で張り実行期限は既定のまま()
    {
        var services = factory.Services;

        services.GetRequiredService<GraphSyncTimeouts>().Should().Be(GraphSyncTimeouts.Default);
        services.GetRequiredService<ConsumerCallTimeouts>().Should().NotBeNull();
        services.GetRequiredService<HandlerGraph>().Chains
            .Where(c => c.MessageType == typeof(DocumentUpdated))
            .Should().ContainSingle().Which.ExecutionTimeoutInSeconds.Should().BeNull();
    }
}
