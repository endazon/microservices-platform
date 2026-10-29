using AwesomeAssertions;
using Knowledge.Contracts.Events;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.Documents.Catalog;

// NFR, #2332: ハーネスの記録の読み方は**待たない**こと。
//
// MassTransit 8.4.1 の同期の列挙（`harness.Consumed.Select<T>(ct)`）は、手元の記録が尽きると取り消しの callback を登録して
// 次の要素を待つ段に入り、そこで届いた要素を lock を握ったまま返す。要素を見ている間に非活動のトークンが取り消されると、
// callback は lock を待ち、こちらは列挙の破棄で callback の完了を待って、互いに止まる（CI job 123724652810 の 30 分打ち切り）。
// 待つ段に入らなければ止まり得ないので、**記録が空でも即時に戻る**ことを上限つきで固定する。
[Trait("TestKind", "Unit")]
public sealed class DocumentNormalizedHarnessWaitTests
{
    private sealed class Sink : IConsumer<DocumentNormalized>
    {
        public Task Consume(ConsumeContext<DocumentNormalized> context) => Task.CompletedTask;
    }

    // 待つ段に入れば TestTimeout（既定 30 秒）か非活動の窓（ここでは 60 秒）まで戻らない。即時かどうかは 5 秒で見分けられる。
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<Sink>();
                x.SetTestTimeouts(testInactivityTimeout: TimeSpan.FromSeconds(60));
            })
            .BuildServiceProvider();

    private static DocumentNormalized Event(Guid id) => new(
        DocumentId: id, SourceId: Guid.NewGuid(), Title: "t", MarkdownUri: "storage://x/document.md",
        AssetUris: [], Attributes: new Dictionary<string, string>(), Tags: [], NormalizedAt: DateTimeOffset.UtcNow);

    [Fact]
    public async Task 記録が空でも消費の読みは待たずに戻る()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var read = Task.Run(() => DocumentNormalizedHarnessWait.FindConsumed(harness, Guid.NewGuid()),
            TestContext.Current.CancellationToken);

        (await Task.WhenAny(read, Task.Delay(Bound, TestContext.Current.CancellationToken))).Should().BeSameAs(read,
            "待つ段に入る読み方は、要素を握ったまま取り消しと重なると止まる");
        (await read).Should().BeNull();
    }

    [Fact]
    public async Task 記録が空でも失敗の読みは待たずに戻る()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var read = Task.Run(() => DocumentNormalizedHarnessWait.AnyFault(harness), TestContext.Current.CancellationToken);

        (await Task.WhenAny(read, Task.Delay(Bound, TestContext.Current.CancellationToken))).Should().BeSameAs(read,
            "待つ段に入る読み方は、要素を握ったまま取り消しと重なると止まる");
        (await read).Should().BeFalse();
    }

    // 陽性対照: 待たない読み方でも、届いた記録は見える（何も読まない実装を緑にしない）。
    [Fact]
    public async Task 届いた消費の記録は待たない読み方でも見える()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var id = Guid.NewGuid();

        await harness.Bus.Publish(Event(id), TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow + Bound;
        IReceivedMessage<DocumentNormalized>? found;
        while ((found = DocumentNormalizedHarnessWait.FindConsumed(harness, id)) is null && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        found.Should().NotBeNull();
        found!.Context.Message.DocumentId.Should().Be(id);
    }
}
