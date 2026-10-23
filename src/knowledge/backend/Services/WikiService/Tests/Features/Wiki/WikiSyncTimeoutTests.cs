using System.Diagnostics;
using AwesomeAssertions;
using Knowledge.Contracts.Events;
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
using Wolverine.Runtime.Handlers;

namespace WikiService.Tests.Features.Wiki;

// FR-13 テスト仕様 (#1640) —— UC-07, ADR-0027, IADR-0021: Wiki.js への同期・撤去の受け口の期限。
//
// 🔴 **縮めた受け口の ct の下で測る。** 受け口の ct は Wolverine の実行期限（両受け口とも既定 60 秒）を含む。ここでは 30 秒の CTS で模し（呼び出しごとの 1 秒と十分に離し、負荷下の揺らぎで比が崩れないようにする）、
// 呼び出しごとの期限（1 秒）が**先に**立って時間切れ（`ConsumerTimeoutException`）になること —— 受け口の ct は立っていないこと —— を見る。
// 期限を外す変異では、止まった依存先は受け口の ct で取り消し（`OperationCanceledException`）として落ち、赤になる。
[Trait("TestKind", "Unit")]
public class WikiSyncTimeoutTests
{
    private static readonly TimeSpan HandlerBudget = TimeSpan.FromSeconds(30);
    private static readonly WikiSyncTimeouts Scaled = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    public enum Hung { Content, Upsert, Archive, Delete }

    private static WikiDbContext NewDb() => new(
        new DbContextOptionsBuilder<WikiDbContext>().UseInMemoryDatabase($"wst_{Guid.NewGuid():N}").Options);

    private static DocumentUpdated Updated(string status) => new(
        Guid.Parse("33333333-3333-3333-3333-333333333333"), "時間切れの試験", status, "storage://b/w.md",
        new Dictionary<string, string> { ["confidentiality"] = "internal" }, ["ops"], DateTimeOffset.UtcNow);

    private static Task HandleAsync(Hung hung, WikiDbContext db, WikiSyncTimeouts timeouts, CancellationToken ct)
    {
        var ports = new HangingPorts(hung);
        if (hung == Hung.Delete)
            return new DocumentDeletedConsumer(db, ports, ports, ConsumerTimeoutsForTests.Calls(), timeouts,
                    new WikiJsWriteGate(1), NullLogger<DocumentDeletedConsumer>.Instance)
                .Handle(new DocumentDeleted(Guid.NewGuid(), DateTimeOffset.UtcNow), ct);

        return new DocumentSyncConsumer(db, ports, ports, ConsumerTimeoutsForTests.Calls(), timeouts,
                new WikiJsWriteGate(1), TestMeterFactory.NewSkippedMetrics(), NullLogger<DocumentSyncConsumer>.Instance)
            .Handle(Updated(hung == Hung.Archive ? "archived" : "published"), ct);
    }

    // 止まった本文の取得・Wiki.js への反映・アーカイブ・撤去は、受け口の ct が立つより前にその呼び出し先の時間切れとして投げる。
    [Theory]
    [InlineData(Hung.Content, WikiSyncTimeouts.ContentTarget)]
    [InlineData(Hung.Upsert, WikiSyncTimeouts.WikiJsTarget)]
    [InlineData(Hung.Archive, WikiSyncTimeouts.WikiJsTarget)]
    [InlineData(Hung.Delete, WikiSyncTimeouts.WikiJsTarget)]
    public async Task 止まった依存先は受け口の期限より前に時間切れとして投げる(Hung hung, string target)
    {
        await using var db = NewDb();
        using var handler = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        handler.CancelAfter(HandlerBudget);
        var started = Stopwatch.GetTimestamp();

        var act = () => HandleAsync(hung, db, Scaled, handler.Token);

        var thrown = (await act.Should().ThrowAsync<ConsumerTimeoutException>()).Which;
        handler.IsCancellationRequested.Should().BeFalse("呼び出しごとの期限が受け口の ct より先に立つ");
        Stopwatch.GetElapsedTime(started).Should().BeLessThan(HandlerBudget);
        thrown.Step.Should().Be(hung == Hung.Delete ? DocumentDeletedConsumer.StepName : DocumentSyncConsumer.StepName);
        thrown.Target.Should().Be(target);
    }

    // 対照: 呼び出し元の取り消しは時間切れに化けず、取り消しのまま外へ出る。
    [Theory]
    [InlineData(Hung.Content)]
    [InlineData(Hung.Upsert)]
    [InlineData(Hung.Delete)]
    public async Task 呼び出し元の取り消しは取り消しのまま外へ出る(Hung hung)
    {
        await using var db = NewDb();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(TimeSpan.FromMilliseconds(200));

        var act = () => HandleAsync(hung, db, new WikiSyncTimeouts(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)),
            caller.Token);

        (await act.Should().ThrowAsync<OperationCanceledException>()).Which
            .Should().NotBeAssignableTo<TimeoutException>();
    }

    private static IConfiguration Config(string? content, string? wikiJs) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [WikiSyncTimeouts.ContentReadKey] = content,
            [WikiSyncTimeouts.WikiJsKey] = wikiJs,
        })
        .Build();

    [Fact]
    public void 構成が無ければ既定の期限になる()
    {
        WikiSyncTimeouts.From(Config(null, null)).Should().Be(WikiSyncTimeouts.Default);
        WikiSyncTimeouts.Default.ContentRead.Should().Be(TimeSpan.FromSeconds(20));
        WikiSyncTimeouts.Default.WikiJs.Should().Be(TimeSpan.FromSeconds(15));
    }

    // 同期（本文 ＋ 門 ＋ Wiki.js）か撤去（門 ＋ Wiki.js）の最悪の所要時間が実行期限（既定 90 秒。［2026-10-23 / #2100］従前は Wolverine の既定 60 秒）以上なら
    // 起動を止める（等しいときも止める）。45 は 45＋30＋15＝90 秒。
    [Theory]
    [InlineData("45", null, "wiki-sync")]
    [InlineData("1", "60", "wiki-")]
    public void 最悪の所要時間が既定の実行期限に収まらなければ起動を止める(string? content, string? wikiJs, string step)
    {
        var act = () => WikiSyncTimeouts.From(Config(content, wikiJs));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{step}*");
    }

    // FR-13, IADR-0563 決定 4 (#1971), IADR-0599 決定 3・4 (#2100): 最悪の所要時間には書き込みの門の待ち（未設定なら Wiki.js 1 回の 2 倍）が入り、
    // 両受け口の実行期限（既定 90 秒）と比べる。門を数えなければ収まる構成（同期 20＋24＝44 秒）も、数えれば 20＋48＋24＝92 秒になるので起動を止める。
    [Fact]
    public void 書き込みの門の待ちも最悪の所要時間に数える()
    {
        var act = () => WikiSyncTimeouts.From(Config("20", "24"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*wiki-sync*92 秒*門の待ち*");
    }

    // 対照: 門を数えても収まる構成は通る（同期 18＋40＋20＝78 秒・撤去 60 秒）。
    [Fact]
    public void 門の待ちを数えても収まる構成は通る()
    {
        var act = () => WikiSyncTimeouts.From(Config("18", "20"));

        act.Should().NotThrow();
    }

    // IADR-0599 決定 3: 門の待ちは既定で Wiki.js 1 回の 2 倍（2 レプリカの最悪）。明示すればその値。実行期限の既定は 90 秒。
    [Fact]
    public void 門の待ちの既定はWikiJs一回の二倍で実行期限の既定は90秒()
    {
        WikiSyncTimeouts.Default.WriteGateWait.Should().Be(TimeSpan.FromSeconds(30));
        WikiSyncTimeouts.Default.Handler.Should().Be(TimeSpan.FromSeconds(90));
        WikiSyncTimeouts.From(Config(null, "10")).WriteGateWait.Should().Be(TimeSpan.FromSeconds(20));

        var configured = WikiSyncTimeouts.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [WikiSyncTimeouts.WriteGateWaitKey] = "45",
            [WikiSyncTimeouts.HandlerKey] = "120",
        }).Build());
        configured.WriteGateWait.Should().Be(TimeSpan.FromSeconds(45));
        configured.Handler.Should().Be(TimeSpan.FromSeconds(120));
    }

    // 実行期限を縮めて最悪の所要時間（既定 65 秒）が収まらなければ止める。再試行の連鎖がブローカの consumer_timeout を超える実行期限も止める。
    [Theory]
    [InlineData("65", null, "wiki-sync")]
    [InlineData("440", null, "consumer_timeout")]
    [InlineData("200", "800", "consumer_timeout")]
    public void 実行期限が最悪の所要時間か再試行の連鎖に合わなければ起動を止める(string handler, string? broker, string message)
    {
        var act = () => WikiSyncTimeouts.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [WikiSyncTimeouts.HandlerKey] = handler,
            [ConsumerHandlerTimeouts.BrokerConsumerTimeoutKey] = broker,
        }).Build());

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{message}*");
    }

    // 指定した 1 つの依存先だけが止まる（ct が立つまで返らない）ポート群。
    private sealed class HangingPorts(Hung hung) : IWikiJsClient, IWikiContentReader
    {
        private async Task HangIf(Hung which, CancellationToken ct)
        {
            if (hung == which)
                await Task.Delay(Timeout.Infinite, ct);
        }

        public async Task<string> ReadAsync(string? markdownUri, string title, CancellationToken ct = default)
        {
            await HangIf(Hung.Content, ct);
            return "# 本文";
        }

        public Task UpsertPageAsync(WikiJsPage page, CancellationToken ct = default) => HangIf(Hung.Upsert, ct);

        public Task<string?> GetRenderedContentAsync(string path, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task ArchivePageAsync(string path, CancellationToken ct = default) => HangIf(Hung.Archive, ct);

        public Task DeletePageAsync(string path, CancellationToken ct = default) => HangIf(Hung.Delete, ct);
    }
}

// 本番の Program.cs の配線: 上限を DI に置き、両受け口の実行期限は `Wiki:HandlerTimeoutSeconds`（既定 90 秒）を方針で与える
// （［2026-10-23 / #2100・IADR-0599 決定 4］従前は Wolverine の既定のまま＝方針なし）。
[Trait("TestKind", "Integration")]
public class WikiSyncTimeoutWiringTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public void 本番の配線は期限を既定値で張り両受け口の実行期限は90秒()
    {
        var services = factory.Services;

        services.GetRequiredService<WikiSyncTimeouts>().Should().Be(WikiSyncTimeouts.Default);
        services.GetRequiredService<ConsumerCallTimeouts>().Should().NotBeNull();
        var chains = services.GetRequiredService<HandlerGraph>().Chains
            .Where(c => c.MessageType == typeof(DocumentUpdated) || c.MessageType == typeof(DocumentDeleted))
            .ToList();
        chains.Should().HaveCount(2);
        chains.Should().OnlyContain(c => c.ExecutionTimeoutInSeconds == WikiSyncTimeouts.DefaultHandlerSeconds);
    }
}
