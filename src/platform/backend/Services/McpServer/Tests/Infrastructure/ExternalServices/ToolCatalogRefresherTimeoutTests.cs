using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using McpServer.Domain;
using McpServer.Infrastructure.ExternalServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpServer.Tests.Infrastructure.ExternalServices;

// 🔴 FR-16, ADR-0024 §2・§5, IADR-0462 の 2026-09-26 追記 (#1604):
// **申告の収集の時間切れ 1 回で McpServer のホスト全体が止まらない。**
//
// 直す前の形は、REST の収集（`HttpToolDeclarationSource.CollectOneAsync`）と `ToolCatalogRefresher` の周期の捕捉が
// `when (ex is not OperationCanceledException)` で取り消しを**型だけで**素通ししていた。HttpClient の時間切れは
// `TaskCanceledException` で表れるので、応答しない宛先 1 つで ExecuteAsync から例外が漏れ、既定の
// `BackgroundServiceExceptionBehavior.StopHost` で `ApplicationStopping` が発火した（監査の使い捨ての試験で再現。
// 3 つの REST の宛先はいずれも既定の経路で、初回の収集は起動直後に走る）。
//
// 本クラスは**汎用ホスト**（既定の StopHost）で本物の `ToolCatalogRefresher` と本物の REST の収集器を動かす ——
// 例外が「ホストを止めるかどうか」は BackgroundService を直接駆動しても観測できない（`ToolPublicationFailFastTests` の注記と同じ理由）。
// 待受は **127.0.0.1 だけ**（0.0.0.0 で待ち受けない）。周期は `CycleInterval`（試験だけが与える口）で短くする。
[Trait("TestKind", "Integration")]
public sealed class ToolCatalogRefresherTimeoutTests
{
    private static readonly TimeSpan ShortCycle = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 🔴 #1604: 接続を受けて何も返さない宛先。期限（1 秒）で打ち切られた収集は「申告なし」へ畳まれ、
    // ホストは止まらず、次の周期の収集も起きる（待受が 2 回目の接続を受け、失敗が 2 回記録される）。
    [Fact]
    public async Task 応答しない宛先の時間切れでホストは止まらず次の周期も収集する()
    {
        using var peer = SilentPeer.Start();
        await using var run = await RefresherRun.StartAsync(RestTarget(peer.Port, timeoutSeconds: 1));

        // 直す前の形では約 1 秒で ApplicationStopping が立つ（待つのをそこで打ち切り、下の表明で赤にする）。
        await run.WaitUntilAsync(() => run.CollectionFailures.Count >= 2);

        run.StoppingFired.Should().BeFalse("時間切れは収集の一時失敗であり、ホストを止めない");
        run.Refresher.ExecuteTask!.IsCompleted.Should().BeFalse("停止要求は出していない。周期は回り続けている");
        peer.Accepted.Should().BeGreaterThanOrEqualTo(2, "次の周期でも同じ宛先へ収集に行っている");
        run.CollectionFailures.Should().AllSatisfy(e => e.Should().BeAssignableTo<OperationCanceledException>(
            "記録されたのは時間切れ（HttpClient.Timeout の TaskCanceledException）である"));
    }

    // 対照 (#1604): 接続を拒否する宛先。直す前から「申告なし」へ畳まれ、ホストは止まらなかった経路である。
    // 期限を 5 秒にするのは、Windows の loopback の拒否が再送で約 2 秒かかり、期限 1 秒だと拒否が時間切れに化けて対照にならないため。
    [Fact]
    public async Task 接続を拒否する宛先でもホストは止まらず次の周期も収集する()
    {
        var port = RefusingPort();
        await using var run = await RefresherRun.StartAsync(RestTarget(port, timeoutSeconds: 5));

        await run.WaitUntilAsync(() => run.CollectionFailures.Count >= 2);

        run.StoppingFired.Should().BeFalse();
        run.Refresher.ExecuteTask!.IsCompleted.Should().BeFalse();
        run.CollectionFailures.Should().AllSatisfy(e => e.Should().BeOfType<HttpRequestException>(
            "拒否として処理されている（時間切れに化けていない）"));
    }

    // 🔴 #1604: 収集器の内側で畳み損ねた取り消し（停止要求ではない）が周期の捕捉まで届いても、ホストは止まらず次の周期へ進む。
    // REST の収集器が時間切れを畳む限り、周期の捕捉の絞り込みは上の試験からは見えない（その変異はこの試験でだけ赤になる）。
    [Fact]
    public async Task 収集器から停止要求でない取り消しが漏れても周期は次の収集へ進む()
    {
        var source = new ThrowOnceSource();
        await using var run = await RefresherRun.StartAsync(new Dictionary<string, string?>(), services =>
            services.AddScoped<IToolDeclarationSource>(_ => source));

        await run.WaitUntilAsync(() => source.Calls >= 2);

        run.StoppingFired.Should().BeFalse();
        run.Refresher.ExecuteTask!.IsCompleted.Should().BeFalse();
        run.RefreshErrors.Should().ContainSingle().Which.Should().BeOfType<TaskCanceledException>(
            "停止要求でない取り消しは周期の失敗として記録する（黙って読み飛ばさない）");
    }

    // 停止要求では従前どおり静かに終わる（例外で終わらない）。
    [Fact]
    public async Task 停止要求では周期は静かに終わる()
    {
        var source = new ThrowOnceSource();
        var run = await RefresherRun.StartAsync(new Dictionary<string, string?>(), services =>
            services.AddScoped<IToolDeclarationSource>(_ => source));
        await run.WaitUntilAsync(() => source.Calls >= 2);

        await run.DisposeAsync();

        run.Refresher.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue("停止要求はシャットダウンとして正常に終える");
    }

    // #1604 (AC-4): 名前付きクライアントの期限は構成で与えられ、既定は 10 秒、1 未満は 1 秒に丸める。
    // gRPC の期限は同じクライアントの `Timeout` を引くので（`GrpcToolDeclarationCollectorTests` T-G8）、期限の出所は 1 つのままである。
    [Theory]
    [InlineData(null, 10)]
    [InlineData("3", 3)]
    [InlineData("0", 1)]
    public void 申告の収集の名前付きクライアントに期限を明示する(string? configured, int expectedSeconds)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [HttpToolDeclarationSource.TimeoutKey] = configured })
            .Build();
        using var sp = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddMcpToolDeclarationSources(configuration)
            .BuildServiceProvider();

        sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpToolDeclarationSource.HttpClientName)
            .Timeout.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    // #1608: 期限のキーは本番の appsettings.json に**周期（`RefreshIntervalSeconds`）と並べて明示する**。
    // 既定値はコードの `DefaultTimeoutSeconds` と同じ値で、運用者が構成ファイルを見ればつまみの在り処と既定が分かる。
    // 本番の構成ファイルを読み込んで登録を通し、名前付きクライアントの期限が 10 秒になることまで見る。
    [Fact]
    public void 本番の構成ファイルは申告の収集の期限を既定値で並べる()
    {
        using var json = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            McpToolsGrpcDeploymentWiringTests.ReadRepoFile(McpToolsGrpcDeploymentWiringTests.AppSettings)));
        var configuration = new ConfigurationBuilder().AddJsonStream(json).Build();

        configuration[ToolCatalogRefresher.IntervalKey].Should().NotBeNull("対照: 周期のキーを読めていないなら以下は何も検査していない");
        configuration.GetValue<int?>(HttpToolDeclarationSource.TimeoutKey).Should().Be(
            HttpToolDeclarationSource.DefaultTimeoutSeconds, "構成ファイルに期限のキーが既定値で並んでいる");

        using var sp = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddMcpToolDeclarationSources(configuration)
            .BuildServiceProvider();
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpToolDeclarationSource.HttpClientName)
            .Timeout.Should().Be(TimeSpan.FromSeconds(10));
    }

    private static Dictionary<string, string?> RestTarget(int port, int timeoutSeconds) => new()
    {
        ["Mcp:Services:document-service"] = $"http://127.0.0.1:{port}",
        [HttpToolDeclarationSource.TimeoutKey] = timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    // 待受を開いてポートを得たあと閉じる（以後そのポートへの接続は拒否される）。
    private static int RefusingPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // 汎用ホスト（既定の StopHost）で本物の ToolCatalogRefresher を動かす器。
    private sealed class RefresherRun : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly RecordingLoggerProvider _logs;
        private volatile bool _stopping;
        private bool _disposed;

        private RefresherRun(IHost host, RecordingLoggerProvider logs)
        {
            _host = host;
            _logs = logs;
            Refresher = host.Services.GetServices<IHostedService>().OfType<ToolCatalogRefresher>().Single();
            host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() => _stopping = true);
        }

        public ToolCatalogRefresher Refresher { get; }

        public bool StoppingFired => _stopping;

        // REST の収集器が「申告なし」へ畳んだ失敗（警告の例外）。
        public IReadOnlyList<Exception?> CollectionFailures =>
            _logs.Exceptions(typeof(HttpToolDeclarationSource).FullName!, LogLevel.Warning);

        // 周期の捕捉が記録した失敗。
        public IReadOnlyList<Exception?> RefreshErrors =>
            _logs.Exceptions(typeof(ToolCatalogRefresher).FullName!, LogLevel.Error);

        public static async Task<RefresherRun> StartAsync(
            Dictionary<string, string?> settings, Action<IServiceCollection>? overrideSource = null)
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            // 公開構成は「無い」（＝公開 0 件で正しい構成）。既定のパス（出力先の Configuration/）に寄りかからない。
            settings[ToolPublicationConfigLoader.PathKey] =
                Path.Combine(Path.GetTempPath(), $"mcp-pub-absent-{Guid.NewGuid():N}.json");
            builder.Configuration.AddInMemoryCollection(settings);
            var logs = new RecordingLoggerProvider();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Logging.SetMinimumLevel(LogLevel.Debug);

            builder.Services.AddSingleton<ToolPublicationConfigLoader>();
            builder.Services.AddSingleton<ToolCatalog>();
            if (overrideSource is null)
                builder.Services.AddMcpToolDeclarationSources(builder.Configuration);
            else
                overrideSource(builder.Services);
            builder.Services.AddHostedService(sp => new ToolCatalogRefresher(
                sp,
                sp.GetRequiredService<ToolCatalog>(),
                sp.GetRequiredService<ToolPublicationConfigLoader>(),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<ILogger<ToolCatalogRefresher>>())
            { CycleInterval = ShortCycle });

            var run = new RefresherRun(builder.Build(), logs);
            await run._host.StartAsync(Ct);
            return run;
        }

        // 条件が立つか、ホストが止まり始めるまで待つ（止まり始めたら待たずに返し、呼び出し側の表明で赤にする）。
        public async Task WaitUntilAsync(Func<bool> condition)
        {
            var until = DateTime.UtcNow + Deadline;
            while (!condition() && !_stopping)
            {
                if (DateTime.UtcNow > until)
                    Assert.Fail($"{Deadline.TotalSeconds} 秒待っても次の周期の収集が起きなかった");
                await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _host.StopAsync(CancellationToken.None);
            _host.Dispose();
        }
    }

    // 接続を受けて何も返さない（読みもしない）127.0.0.1 の待受。
    private sealed class SilentPeer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly ConcurrentBag<TcpClient> _clients = [];
        private readonly CancellationTokenSource _cts = new();
        private int _accepted;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Accepted => Volatile.Read(ref _accepted);

        public static SilentPeer Start()
        {
            var peer = new SilentPeer();
            peer._listener.Start();
            _ = Task.Run(peer.AcceptLoopAsync);
            return peer;
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    _clients.Add(await _listener.AcceptTcpClientAsync(_cts.Token));
                    Interlocked.Increment(ref _accepted);
                }
            }
            catch (Exception) { /* 停止 */ }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            foreach (var c in _clients) c.Dispose();
            _cts.Dispose();
        }
    }

    // 1 回目だけ停止要求と無関係な取り消し（HttpClient の時間切れと同じ型）を投げ、以後は申告なしを返す。
    private sealed class ThrowOnceSource : IToolDeclarationSource
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<IReadOnlyList<ServiceToolDeclarations>> CollectAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                return Task.FromException<IReadOnlyList<ServiceToolDeclarations>>(
                    new TaskCanceledException("下流の時間切れ（停止要求ではない）"));
            return Task.FromResult<IReadOnlyList<ServiceToolDeclarations>>([]);
        }
    }

    // カテゴリと水準ごとに例外を記録する（新規パッケージを増やさないため手書きする）。
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<(string Category, LogLevel Level, Exception? Exception)> _entries = new();

        public IReadOnlyList<Exception?> Exceptions(string category, LogLevel level) =>
            [.. _entries.Where(e => e.Category == category && e.Level == level).Select(e => e.Exception)];

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

        public void Dispose() { }

        private sealed class Logger(
            string category, ConcurrentQueue<(string, LogLevel, Exception?)> entries) : ILogger
        {
            IDisposable? ILogger.BeginScope<TState>(TState state) => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => entries.Enqueue((category, logLevel, exception));
        }
    }
}
