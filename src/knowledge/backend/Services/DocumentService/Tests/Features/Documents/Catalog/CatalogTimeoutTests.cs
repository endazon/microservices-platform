using System.Diagnostics;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using DocumentService.Common.Observability;
using DocumentService.Domain.Ports;
using DocumentService.Features.Documents.Catalog;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Infrastructure.Foundation.Messaging;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace DocumentService.Tests.Features.Documents.Catalog;

// FR-06 テスト仕様 T-71 (#1657) —— FR-12, ADR-0027, ADR-0050: カタログ登録の受け口（MassTransit）の本文の取得の期限。
//
// 🔴 **縮めた受け口の ct の下で測る。** 本受け口（MassTransit）は 1 通ごとの実行期限を持たず、受け口の ct はバスの停止でしか立たない。
// ここでは 30 秒の CTS で受け口の ct を模し（呼び出しごとの 1 秒と 30 倍離し、負荷下の揺らぎで比が崩れないようにする）、
// 本文の取得の期限（1 秒）が**先に**立って時間切れ（`ConsumerTimeoutException`）になること —— 受け口の ct は立っていないこと —— を見る。
// 期限を外す変異では、止まったストレージは受け口の ct で取り消し（`OperationCanceledException`）として落ち、赤になる。
[Trait("TestKind", "Unit")]
public class CatalogTimeoutTests
{
    private static readonly TimeSpan HandlerBudget = TimeSpan.FromSeconds(30);

    private static DocumentDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<DocumentDbContext>().UseInMemoryDatabase(name).Options);

    private static DocumentNormalizedConsumer Build(
        DocumentDbContext db, RecordingUpdatedPublisher publisher, TimeSpan contentRead, ConsumerCallTimeouts calls) => new(
        db, publisher, new HangingStorage(), new IngestTagMetrics(new TestMeterFactory()),
        calls, new CatalogTimeouts(contentRead), NullLogger<DocumentNormalizedConsumer>.Instance);

    private static DocumentNormalized Event(Guid id) => new(
        DocumentId: id,
        SourceId: Guid.NewGuid(),
        Title: "時間切れの試験",
        MarkdownUri: $"storage://knowledge-normalized/{id:N}/document.md",
        AssetUris: [],
        Attributes: new Dictionary<string, string> { ["confidentiality"] = "internal" },
        Tags: [],
        NormalizedAt: DateTimeOffset.UtcNow);

    // 止まったストレージは受け口の ct が立つより前に時間切れとして投げ、**指紋の「不明（null）」へ畳まない** ——
    // カタログへ保存せず発行もしない（再試行で本文を取り直す）。時間切れは計器に 1 回数えられる。
    [Fact]
    public async Task 止まった本文の取得は時間切れとして投げ保存も発行もしない()
    {
        var id = Guid.NewGuid();
        var name = $"catalog-timeout-{Guid.NewGuid():N}";
        var meters = new TestMeterFactory();
        using var probe = new TimeoutProbe(meters);
        var calls = new ConsumerCallTimeouts(new ConsumerTimeoutMetrics(meters), NullLogger<ConsumerCallTimeouts>.Instance);
        var publisher = new RecordingUpdatedPublisher();

        await using (var db = NewDb(name))
        {
            var consumer = Build(db, publisher, TimeSpan.FromSeconds(1), calls);
            using var handler = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            handler.CancelAfter(HandlerBudget);
            var started = Stopwatch.GetTimestamp();

            var act = () => consumer.ConsumeAsync(Event(id), handler.Token);

            var thrown = (await act.Should().ThrowAsync<ConsumerTimeoutException>()).Which;
            handler.IsCancellationRequested.Should().BeFalse("本文の取得の期限が受け口の ct より先に立つ");
            Stopwatch.GetElapsedTime(started).Should().BeLessThan(HandlerBudget);
            thrown.Step.Should().Be(DocumentNormalizedConsumer.StepName);
            thrown.Target.Should().Be(CatalogTimeouts.ContentTarget);
            thrown.Limit.Should().Be(TimeSpan.FromSeconds(1));
        }

        probe.Timeouts.Should().ContainSingle().Which.Should().Be(("catalog", "content"));
        publisher.Published.Should().BeEmpty("時間切れの登録は発行しない");
        await using var fresh = NewDb(name);
        (await fresh.Documents.AnyAsync(d => d.Id == id, TestContext.Current.CancellationToken))
            .Should().BeFalse("時間切れの登録は保存しない（再試行で取り直す）");
    }

    // 対照: 呼び出し元の取り消し（バスの停止）は時間切れに化けず、取り消しのまま外へ出る。計器にも数えない。
    [Fact]
    public async Task 呼び出し元の取り消しは取り消しのまま外へ出る()
    {
        var meters = new TestMeterFactory();
        using var probe = new TimeoutProbe(meters);
        var calls = new ConsumerCallTimeouts(new ConsumerTimeoutMetrics(meters), NullLogger<ConsumerCallTimeouts>.Instance);
        await using var db = NewDb($"catalog-cancel-{Guid.NewGuid():N}");
        var consumer = Build(db, new RecordingUpdatedPublisher(), TimeSpan.FromSeconds(30), calls);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(TimeSpan.FromMilliseconds(200));

        var act = () => consumer.ConsumeAsync(Event(Guid.NewGuid()), caller.Token);

        (await act.Should().ThrowAsync<OperationCanceledException>()).Which
            .Should().NotBeAssignableTo<TimeoutException>();
        probe.Timeouts.Should().BeEmpty("取り消しは時間切れとして数えない");
    }

    private static IConfiguration Config(string? contentSeconds, string? brokerSeconds = null) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [CatalogTimeouts.ContentReadKey] = contentSeconds,
            [ConsumerHandlerTimeouts.BrokerConsumerTimeoutKey] = brokerSeconds,
        })
        .Build();

    [Fact]
    public void 構成が無ければ既定の期限になる()
    {
        CatalogTimeouts.From(Config(null)).Should().Be(CatalogTimeouts.Default);
        CatalogTimeouts.Default.ContentRead.Should().Be(TimeSpan.FromSeconds(20));
        CatalogTimeouts.ContentReadKey.Should().Be("DocumentCatalog:ContentReadTimeoutSeconds");
        CatalogTimeouts.From(Config("45")).ContentRead.Should().Be(TimeSpan.FromSeconds(45));
    }

    // 1 回の配信の再試行の連鎖（MassTransit の試行上限 4 × 本文の期限 ＋ 試行間の待ち 2＋10＋30 秒）が
    // ブローカの consumer_timeout 以上なら起動を止める（等しいときも止める）。
    // 既定の consumer_timeout 1800 秒: 本文 439 秒は 4 × 439 ＋ 42 ＝ 1798 で通り、440 秒は 1802 で止まる。
    // consumer_timeout を 122 秒に置くと既定（4 × 20 ＋ 42 ＝ 122）と等しく止まり、123 秒なら通る。
    [Theory]
    [InlineData("439", null, true)]
    [InlineData("440", null, false)]
    [InlineData(null, "122", false)]
    [InlineData(null, "123", true)]
    public void 再試行の連鎖がブローカの_consumer_timeout_に収まらなければ起動を止める(
        string? contentSeconds, string? brokerSeconds, bool fits)
    {
        var act = () => CatalogTimeouts.From(Config(contentSeconds, brokerSeconds));

        if (fits)
            act.Should().NotThrow();
        else
            act.Should().Throw<InvalidOperationException>().WithMessage("*catalog*consumer_timeout*");
    }

    // 止まったストレージ（本文の取得が ct が立つまで返らない）。
    private sealed class HangingStorage : IObjectStorageClient
    {
        public Task<string> PutTextAsync(string key, string text, string contentType, CancellationToken ct = default)
            => Task.FromResult($"storage://test/{key}");
        public Task<string> PutBytesAsync(string key, byte[] bytes, string contentType, CancellationToken ct = default)
            => Task.FromResult($"storage://test/{key}");
        public Task DeleteAsync(string uri, CancellationToken ct = default) => Task.CompletedTask;
        public bool CanResolve(string? uri) => true;

        public async Task<string> GetTextAsync(string uri, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return string.Empty;
        }

        public Task<byte[]> GetBytesAsync(string uri, CancellationToken ct = default) => throw new NotSupportedException();
        public string CreatePresignedGetUrl(string uri, TimeSpan? expiry = null) => throw new NotSupportedException();
    }

    private sealed class RecordingUpdatedPublisher : IDocumentUpdatedPublisher
    {
        public List<Guid> Published { get; } = [];

        public Task PublishUpdatedAsync(Guid documentId, string title, string status, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags, DateTimeOffset updatedAt,
            string? contentFingerprint = null, bool hasBody = true,
            string? originalPath = null, string? dataSourceName = null,
            List<string>? sharedWith = null,
            CancellationToken ct = default)
        {
            Published.Add(documentId);
            return Task.CompletedTask;
        }
    }

    // Meter を試験ごとに作り分ける（`MeterListener` はプロセス全体を購読するので、同じ名前の Meter の測定を
    // 自分の factory が作ったものだけに絞る）。
    private sealed class TestMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) =>
            new($"{options.Name}.test-{Guid.NewGuid():N}", options.Version, options.Tags, scope: this);

        public void Dispose() { }
    }

    private sealed class TimeoutProbe : IDisposable
    {
        private readonly MeterListener _listener = new();

        public List<(string Step, string Target)> Timeouts { get; } = [];

        public TimeoutProbe(IMeterFactory scope)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, scope)
                    && instrument.Name == ConsumerTimeoutMetrics.TimeoutCounterName)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                string? step = null, target = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == ConsumerTimeoutMetrics.StepTag) step = tag.Value as string;
                    if (tag.Key == ConsumerTimeoutMetrics.TargetTag) target = tag.Value as string;
                }
                lock (Timeouts) Timeouts.Add((step!, target!));
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }
}

// 本番の Program.cs の配線: 本文の期限と時間切れの部品を DI に置き、受け口がそれで組み立てられる。
[Trait("TestKind", "Integration")]
public class CatalogTimeoutWiringTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public void 本番の配線は本文の期限を既定値で張り受け口を組み立てられる()
    {
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        services.GetRequiredService<CatalogTimeouts>().Should().Be(CatalogTimeouts.Default);
        services.GetRequiredService<ConsumerCallTimeouts>().Should().NotBeNull();
        // 試験の器は MassTransit をハーネスへ差し替えて受け口を登録しないので、本番の依存から直接組み立てる。
        ActivatorUtilities.CreateInstance<DocumentNormalizedConsumer>(services).Should().NotBeNull();
    }
}
