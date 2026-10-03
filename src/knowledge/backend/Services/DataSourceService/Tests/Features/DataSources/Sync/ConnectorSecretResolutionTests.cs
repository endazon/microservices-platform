using System.Data.Common;
using System.Net;
using System.Text;
using AwesomeAssertions;
using DataSourceService.Domain;
using DataSourceService.Domain.Ports;
using DataSourceService.Features.DataSources.Sync;
using DataSourceService.Infrastructure.ExternalServices;
using DataSourceService.Infrastructure.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace DataSourceService.Tests.Features.DataSources.Sync;

// FR-01, UC-04, NFR-18, 09_datasource-connectors §認証・秘匿情報, [[IADR-0493]] (#458 段 S0):
// コネクタの資格情報は同期の開始時に `IConnectorSecretResolver` で 1 回だけ解決され、
// 解決できなければ外部へ要求を出さずに失敗する（fail-closed）ことを、**実コネクタ**で測る。
//
// 作業仕様書 20261003_458 §設計 S0 の試験欄 4 本の写像:
//   ① 3 コネクタが Config を直接読まない（解決器の呼び出しを数える）
//      → `Sync_RealConnector_ResolvesOnce_AndSendsOnlyTheResolvedValue`（wiki / saas / db）
//        ＋ 各コネクタの単体試験 `*_IgnoreConfig*_WhenCredentialsAreNotResolved`
//   ② 解決失敗で「資格情報未設定」の失敗状態になり、外部へ要求を出さない
//      → `Sync_UnresolvableCredential_FailsClosed_WithoutAnyOutboundRequest` ／ `Sync_Db_UnresolvableCredential_NeverOpensAConnection`
//   ③ 解決失敗のログ・SyncError に値も参照の内部も出ない
//      → `Sync_ResolutionFailure_LeaksNeitherValueNorReferencePath`
//   ④ 同期の途中で版が変わっても 1 回の同期で混ざらない（§窓 2）
//      → `Sync_VersionChangeMidSync_DoesNotMixWithinOneRun_AndTheNextRunUsesTheNewVersion`
[Trait("TestKind", "Integration")]
public sealed class ConnectorSecretResolutionTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private const string WikiBase = "https://wiki.example.test";
    private const string SaasBase = "https://saas.example.test";
    private const string Reference = "vault:datasource/ds-1#apiToken";

    // ① 実コネクタ 3 種: 1 回の同期で解決器は**ちょうど 1 回**呼ばれ、外へ出る要求はすべて解決済みの値を運ぶ。
    // `Config` には参照を置く —— コネクタが `Config` を直接読めば参照の文字列がそのまま送られて落ちる。
    [Theory]
    [InlineData("wiki")]
    [InlineData("saas")]
    public async Task Sync_RealConnector_ResolvesOnce_AndSendsOnlyTheResolvedValue(string sourceType)
    {
        var resolver = new FakeConnectorSecretResolver { References = { [Reference] = "resolved-http-value" } };
        var handler = new CountingHandler(sourceType);
        var (connector, source) = HttpConnectorAndSource(sourceType, handler, Reference);
        var svc = BuildService(connector, resolver);

        var result = await svc.SyncAsync(source, TestContext.Current.CancellationToken);

        result.Fetched.Should().Be(2, "探索 1 回 ＋ 取得 2 件が走る（前提）");
        resolver.Calls.Should().Equal([Reference], "資格情報は同期の開始時に 1 回だけ解決する（Discover / Fetch ごとではない）");
        handler.Requests.Should().HaveCount(3);
        handler.Requests.Should().AllSatisfy(r =>
        {
            r.Headers.Authorization!.Scheme.Should().Be("Bearer");
            r.Headers.Authorization.Parameter.Should().Be("resolved-http-value");
        });
    }

    [Fact]
    public async Task Sync_RealDbConnector_ResolvesOnce_AndComposesOnlyTheResolvedPassword()
    {
        var resolver = new FakeConnectorSecretResolver
        {
            References = { ["vault:datasource/ds-1#password"] = "resolved-db-value" },
        };
        var connections = new RecordingConnectionFactory();
        var svc = BuildService(new DatabaseConnector(connections, NullLogger<DatabaseConnector>.Instance), resolver);

        await svc.SyncAsync(DbSource("vault:datasource/ds-1#password"), TestContext.Current.CancellationToken);

        resolver.Calls.Should().Equal(["vault:datasource/ds-1#password"]);
        connections.ConnectionStrings.Should().ContainSingle();
        new DbConnectionStringBuilder { ConnectionString = connections.ConnectionStrings[0] }["Password"]
            .Should().Be("resolved-db-value", "接続文字列へ合成するのは解決済みの値であり、Config の参照ではない");
    }

    // ② fail-closed: 解決できない理由ごとに、外部へ 1 件も要求を出さず「資格情報未設定」の失敗状態で止まる。
    public static TheoryData<string, string, string> Failures => new()
    {
        // (ケース, Config の値, 期待する理由の符号)
        { "表に無い参照", Reference, "not-found" },
        { "解決器が配備されていない（移送期間用の解決器に参照）", Reference, "resolver-unavailable" },
        { "形を成していない参照", "vault:datasource/ds-1", "malformed-reference" },
        { "大文字の接頭辞（平文として素通ししない）", "VAULT:datasource/ds-1#apiToken", "resolver-unavailable" },
        { "解決した値が空", Reference, "empty" },
        { "解決器が例外", Reference, "unreachable" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Sync_UnresolvableCredential_FailsClosed_WithoutAnyOutboundRequest(
        string @case, string configured, string code)
    {
        var resolver = ResolverFor(code);
        var handler = new CountingHandler("wiki");
        var (connector, source) = HttpConnectorAndSource("wiki", handler, configured);
        var svc = BuildService(connector, resolver);

        var result = await svc.SyncAsync(source, TestContext.Current.CancellationToken);

        handler.Requests.Should().BeEmpty($"{@case}: 解決できない資格情報で外部へ要求を出さない（平文へも認証なしへも倒さない）");
        result.CredentialsResolved.Should().BeFalse();
        result.DiscoverSucceeded.Should().BeFalse("探索は走っていない");
        result.ShouldAdvanceWatermark.Should().BeFalse();
        result.Message.Should().Be($"credentials not resolved for 'apiToken' ({code})");
        source.LastSyncError.Should().Be(result.Message, "SC-06 が読む直近エラーに同じ状態が載る");
        source.ConsecutiveFailureCount.Should().Be(1, "同期の失敗として数える（継続失敗アラートの対象）");
        source.LastSyncedAt.Should().BeNull();
    }

    [Fact]
    public async Task Sync_Db_UnresolvableCredential_NeverOpensAConnection()
    {
        var connections = new RecordingConnectionFactory();
        var svc = BuildService(
            new DatabaseConnector(connections, NullLogger<DatabaseConnector>.Instance),
            new PlaintextPassthroughConnectorSecretResolver());

        var result = await svc.SyncAsync(DbSource("vault:datasource/ds-1#password"), TestContext.Current.CancellationToken);

        connections.ConnectionStrings.Should().BeEmpty("解決できない資格情報で業務 DB へ接続しない");
        result.Message.Should().Be("credentials not resolved for 'password' (resolver-unavailable)");
    }

    // 対照: 資格情報が `Config` に無いソースは従前どおり認証なしで同期する（解決器を呼ばない。失敗にしない）。
    // これが無いと「資格情報を宣言するコネクタは常に止める」実装でも ② が緑になる。
    [Fact]
    public async Task Sync_NoCredentialConfigured_DoesNotCallResolver_AndSyncsWithoutAuthorization()
    {
        var resolver = new FakeConnectorSecretResolver();
        var handler = new CountingHandler("wiki");
        var (connector, source) = HttpConnectorAndSource("wiki", handler, configured: null);

        var result = await BuildService(connector, resolver).SyncAsync(source, TestContext.Current.CancellationToken);

        result.CredentialsResolved.Should().BeTrue();
        result.Fetched.Should().Be(2);
        resolver.Calls.Should().BeEmpty();
        handler.Requests.Should().AllSatisfy(r => r.Headers.Authorization.Should().BeNull());
    }

    // ③ 解決失敗のログ・`SyncError`・応答に、値も参照のパスも解決器の例外文も出ない。
    // 解決器の例外文には値とパスを入れておく（陽性対照: 素直に `ex.Message` を出せば必ず捕まる）。
    [Theory]
    [InlineData("unreachable")]
    [InlineData("not-found")]
    [InlineData("resolver-unavailable")]
    public async Task Sync_ResolutionFailure_LeaksNeitherValueNorReferencePath(string code)
    {
        const string configured = "vault:datasource/hidden-path-segment#apiToken";
        var resolver = ResolverFor(code,
            leakyMessage: "lookup of datasource/hidden-path-segment failed; last value was leaked-value-sample");
        var log = new RecordingLogger();
        var handler = new CountingHandler("wiki");
        var (connector, source) = HttpConnectorAndSource("wiki", handler, configured);

        var result = await BuildService(connector, resolver, log).SyncAsync(source, TestContext.Current.CancellationToken);

        log.Records.Should().NotBeEmpty("失敗は記録される（陰性の主張が空でないこと）");
        var surfaces = log.Records.Select(r => r.Message)
            .Append(result.Message!)
            .Append(source.LastSyncError!)
            .ToList();
        foreach (var text in surfaces)
        {
            text.Should().NotContain("hidden-path-segment", "参照のパスは Vault の構造を漏らす");
            text.Should().NotContain("leaked-value-sample", "解決器の例外文は値を運び得る");
            text.Should().NotContain("vault:", "参照の文字列そのものを出さない（キー名だけ）");
        }
        log.Records.Should().AllSatisfy(r => r.Exception.Should().BeNull("例外オブジェクトを渡すと ToString() が全文を出す"));
    }

    // ④ §窓 2: 1 回目の同期の探索の最中に版が進んでも、その同期の Discover と全 Fetch は古い版のまま完走し、
    // 次の同期は新しい版を使う。解決器は同期ごとに 1 回だけ呼ばれる。
    [Fact]
    public async Task Sync_VersionChangeMidSync_DoesNotMixWithinOneRun_AndTheNextRunUsesTheNewVersion()
    {
        var resolver = new FakeConnectorSecretResolver { References = { [Reference] = "token" }, AppendVersion = true };
        var connector = new RecordingConnector(onDiscover: () => resolver.Version++);
        var svc = BuildService(connector, resolver);
        var source = DataSource.Create("ds", RecordingConnector.Type, "",
            new Dictionary<string, string> { ["apiToken"] = Reference });

        await svc.SyncAsync(source, TestContext.Current.CancellationToken);
        var firstRun = connector.Seen.ToList();
        connector.Seen.Clear();
        await svc.SyncAsync(source, TestContext.Current.CancellationToken);

        firstRun.Should().Equal(["token-v1", "token-v1", "token-v1"],
            "探索 1 回 ＋ 取得 2 件のすべてが開始時の版を使う（途中の書き込みで混ざらない）");
        connector.Seen.Should().Equal(["token-v2", "token-v2", "token-v2"], "書き込み後に始まった同期は新しい版を使う");
        resolver.Calls.Should().HaveCount(2, "同期ごとに 1 回");
    }

    // ---- helpers ----------------------------------------------------------

    // 理由の符号ごとの解決器。`resolver-unavailable` / `malformed-reference` は**実物の**移送期間用解決器に判定させる
    // （段 S0 で本番に配線されるのはそれであり、参照を平文として素通ししないことを実物で測る）。
    private static IConnectorSecretResolver ResolverFor(string code, string? leakyMessage = null) => code switch
    {
        "not-found" => new FakeConnectorSecretResolver(),
        "empty" => new FakeConnectorSecretResolver { References = { [Reference] = "  " } },
        "unreachable" => new FakeConnectorSecretResolver
        {
            Throw = new HttpRequestException(leakyMessage ?? "vault unreachable"),
        },
        _ => new PlaintextPassthroughConnectorSecretResolver(),
    };

    private DataSourceSyncService BuildService(
        IDataSourceConnector connector, IConnectorSecretResolver resolver, ILogger<DataSourceSyncService>? log = null)
    {
        using var scope = factory.Services.CreateScope();
        return new DataSourceSyncService(
            new ConnectorRegistry([connector]),
            scope.ServiceProvider.GetRequiredService<IObjectStorageClient>(),
            scope.ServiceProvider.GetRequiredService<RecordingMessageBus>(),
            resolver,
            log ?? NullLogger<DataSourceSyncService>.Instance);
    }

    private static (IDataSourceConnector Connector, DataSource Source) HttpConnectorAndSource(
        string sourceType, CountingHandler handler, string? configured)
    {
        var config = configured is null ? [] : new Dictionary<string, string> { ["apiToken"] = configured };
        var http = new StubFactory(handler);
        return sourceType switch
        {
            "wiki" => (new WikiConnector(http, NullLogger<WikiConnector>.Instance),
                DataSource.Create("wiki", "wiki", WikiBase, config)),
            _ => (new SaaSConnector(http, NullLogger<SaaSConnector>.Instance),
                DataSource.Create("saas", "saas", SaasBase, config)),
        };
    }

    private static DataSource DbSource(string password)
        => DataSource.Create("erp", "db", "Host=erp.example.test;Database=orders;Username=readonly",
            new Dictionary<string, string>
            {
                ["query"] = "SELECT id AS id, updated AS updated, body AS content FROM articles",
                ["password"] = password,
            });

    // 一覧 2 件・本文を返す HTTP 応答。wiki と saas の契約の形だけを返し分ける。
    private sealed class CountingHandler(string sourceType) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;
            var list = sourceType == "wiki"
                ? """[{"id":"a","updatedAt":"2026-07-01T00:00:00Z"},{"id":"b","updatedAt":"2026-07-01T00:00:00Z"}]"""
                : """{"items":[{"id":"a","updatedAt":"2026-07-01T00:00:00Z"},{"id":"b","updatedAt":"2026-07-01T00:00:00Z"}],"nextCursor":null}""";
            var isList = sourceType == "wiki" ? path == "/api/pages" : path == "/api/items";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = isList
                    ? new StringContent(list, Encoding.UTF8, "application/json")
                    : new StringContent("# Body", Encoding.UTF8, "text/markdown"),
            });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    // 接続文字列を記録し、接続そのものは失敗させる（業務 DB へ実際には繋がない）。
    private sealed class RecordingConnectionFactory : IDbConnectionFactory
    {
        public List<string> ConnectionStrings { get; } = [];

        public DbConnection Create(string connectionString)
        {
            ConnectionStrings.Add(connectionString);
            throw new InvalidOperationException("test: no database");
        }
    }

    // 受け取った資格情報を Discover と各 Fetch で記録するコネクタ。探索の最中に `onDiscover` を呼ぶ
    // （同期の途中で Vault の版が変わることを模す）。
    private sealed class RecordingConnector(Action onDiscover) : IDataSourceConnector
    {
        public const string Type = "recording";
        public string SourceType => Type;
        public IReadOnlyList<string> CredentialKeys => ["apiToken"];
        public List<string?> Seen { get; } = [];

        public Task<IReadOnlyList<SourceItem>> DiscoverAsync(
            DataSource s, ConnectorCredentials c, DateTimeOffset? since, CancellationToken ct)
        {
            Seen.Add(c.Get("apiToken"));
            onDiscover();
            return Task.FromResult<IReadOnlyList<SourceItem>>(
            [
                new SourceItem("/x/a.md", DateTimeOffset.UtcNow, 1),
                new SourceItem("/x/b.md", DateTimeOffset.UtcNow, 1),
            ]);
        }

        public Task<RawContent> FetchAsync(DataSource s, ConnectorCredentials c, SourceItem item, CancellationToken ct)
        {
            Seen.Add(c.Get("apiToken"));
            return Task.FromResult(new RawContent([1], "text/markdown"));
        }
    }

    private sealed class RecordingLogger : ILogger<DataSourceSyncService>
    {
        public List<(string Message, Exception? Exception)> Records { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Records.Add((formatter(state, exception), exception));
    }
}
