using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using DataSourceService.Domain;
using DataSourceService.Domain.Ports;
using DataSourceService.Features.DataSources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace DataSourceService.Tests;

// FR-01, FR-05, UC-04, SC-06, ADR-0005, IADR-0295 (#458):
// **コネクタ資格情報の、塞がれていなかった露出経路 4 本の陽性対照。**
//
// 🔴 **これらはすべて「秘密を実際に通す」テストである。** 秘密を通さないテストは
// 「実データで緑」になるだけで**検出力の証拠にならない** —— マスクを外しても緑のままだからである。
// 各テストは対応するマスクを外す変異で落ちることを実測して置いている（変異の一覧は
// 作業仕様書 `20260828_issue-458_connector-credential-exposure.md` §テスト方針）。
//
// 経路:
//   (a) 手動同期 API の応答が生の例外メッセージを返していた（`SyncErrorRedactor` を通っていなかった）
//   (b) `ConnectionUri` が一切マスクされていなかった（`SecretConfigMask` は `Config` にしか掛からない）
//   (c) 秘密キーのマーカー集合が 2 箇所にあり食い違っていた（`apiKey` / `pwd` / `privateKey` が素通し）
//   (d) 例外オブジェクトがそのままログへ渡されていた（`Exception.ToString()` が LogRecord に入る）
public class DataSourceCredentialExposureTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    // 実際の資格情報を模した、他の語と衝突しない値。**応答・ログの全文からこの文字列を探す。**
    private const string LeakedPassword = "hunter2-CONNECTOR-SECRET";

    // ---- (c) マーカー集合の統合 -------------------------------------------------------

    // 🔴 **計画が名指しする形式が現行マーカーで捕まらなかった。**
    // 計画 `06_technical/09_datasource-connectors.md` は SaaS の認証を「OAuth／APIキー」と定めるが、
    // 従前の `SecretConfigMask` のマーカーは token / password / secret / credential の 4 語だけで、
    // **`apiKey` は 1 つも当たらなかった**（`SyncErrorRedactor` 側だけが `api[-_]?key` を持っていた）。
    [Theory]
    [InlineData("apiKey")]        // 計画が名指しする形式
    [InlineData("api_key")]
    [InlineData("api-key")]
    [InlineData("pwd")]           // SyncErrorRedactor 側にだけあった
    [InlineData("privateKey")]    // どちらの集合でも捕まらなかった
    [InlineData("private_key")]
    [InlineData("authorization")] // SyncErrorRedactor 側にだけあった
    [InlineData("apiToken")]      // 従来から捕まっていた形（退行していないこと）
    [InlineData("clientSecret")]
    public async Task Get_MasksSecretConfigKey_AcrossUnifiedMarkerSet(string secretKey)
    {
        var client = factory.CreateClient();
        var id = await CreateAsync(client, new Dictionary<string, string>
        {
            [secretKey] = LeakedPassword,
            ["listPath"] = "/api/pages",
        });

        var body = await GetRawAsync(client, id);

        body.Should().NotContain(LeakedPassword,
            $"config のキー '{secretKey}' は秘密として扱いマスクする（マーカー集合は SecretMask ただ 1 本）");
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("config").GetProperty(secretKey).GetString().Should().Be("***");
    }

    // **過剰マスクもまた欠陥である。** 非秘密キーを伏せると原因の切り分けが潰れる。
    // `spaceKey` は `key` を含むため、マーカーに `key` 単独を足すと誤マスクされる（だから足していない）。
    [Theory]
    [InlineData("spaceKey")]
    [InlineData("listPath")]
    [InlineData("rootPath")]
    [InlineData("contentType")]
    [InlineData("cursorParam")]
    public async Task Get_DoesNotMaskNonSecretConfigKey(string plainKey)
    {
        var client = factory.CreateClient();
        var id = await CreateAsync(client, new Dictionary<string, string> { [plainKey] = "plain-value" });

        using var doc = JsonDocument.Parse(await GetRawAsync(client, id));

        doc.RootElement.GetProperty("config").GetProperty(plainKey).GetString()
            .Should().Be("plain-value", $"'{plainKey}' は秘密ではない。伏せると切り分けが潰れる");
    }

    // 読み（マスク）と書き（マスク値を保存しない）が**同じマーカー集合**を使っていること。
    // 片方だけ広げると、広げたキーで「読んで書き戻す」往復が資格情報を破壊する（IADR-0148 決定 6）。
    [Fact]
    public async Task Patch_WritingBackMaskedValue_PreservesSecret_ForNewlyCoveredMarker()
    {
        var client = factory.CreateClient();
        var id = await CreateAsync(client, new Dictionary<string, string> { ["apiKey"] = LeakedPassword });

        // GET の応答（"***"）をそのまま書き戻す。
        var patch = await client.PatchAsJsonAsync($"/datasources/{id}", new
        {
            config = new Dictionary<string, string> { ["apiKey"] = "***" },
        }, TestContext.Current.CancellationToken);
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        // 実値が保たれていること（応答からは見えないので DB を直接見る）。
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.DataSourceDbContext>();
        var stored = await db.DataSources.FindAsync([id], TestContext.Current.CancellationToken);
        stored!.Config["apiKey"].Should().Be(LeakedPassword,
            "マスク値の書き戻しで実値を壊さない（読みと書きが同じマーカー集合を使う）");
    }

    // ---- (b) ConnectionUri のマスク ---------------------------------------------------

    // 🔴 **`ConnectionUri` は `Config` と並ぶ 2 本目の平文の器なのに、応答で素通しだった。**
    // `SecretMask` の URI 規則が `scheme://user:pass@host` を明示的に想定して伏せている以上、
    // **そこへ資格情報が入り得ることをコード自身が認めている。**
    [Fact]
    public async Task Get_MasksCredentialsInsideConnectionUri_OfLegacyRow()
    {
        var client = factory.CreateClient();
        // 検証の口からは入れられないので（下のテストが 400 を固定する）、既存行を模して直接投入する。
        var id = SeedLegacyRow($"postgresql://svc-account:{LeakedPassword}@db.example.test/kb");

        var body = await GetRawAsync(client, id);

        body.Should().NotContain(LeakedPassword, "connectionUri に含まれる資格情報も応答では伏せる");
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("connectionUri").GetString()
            .Should().Be("postgresql://***@db.example.test/kb", "接続先は残し資格情報だけを伏せる");
    }

    // `DatabaseConnector` は `ConnectionUri` を ADO.NET 接続文字列の土台に使う
    // （`DbConnectionStringBuilder { ConnectionString = baseConn }`）ので、キー=値 形式も入り得る。
    [Fact]
    public async Task List_MasksConnectionStringStyleSecret_InConnectionUri()
    {
        var client = factory.CreateClient();
        SeedLegacyRow($"Host=db.example.test;Username=app;Password={LeakedPassword};Database=kb");

        var list = await client.GetAsync("/datasources", TestContext.Current.CancellationToken);
        list.EnsureSuccessStatusCode();
        var json = await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        json.Should().NotContain(LeakedPassword, "一覧応答の connectionUri にも平文の秘密を含めない");
        json.Should().Contain("Password=***", "キー名は残し値だけを伏せる（切り分けには「どの項目か」が要る）");
    }

    // ---- (b) 書き込み時の扱い ---------------------------------------------------------

    // **弾く**と決めた（警告にしない）。警告は平文が DB に入るのを止めないうえ、警告の出口は
    // ログ＝(d) で塞ごうとしている経路そのものである。実測でも、資格情報つきの connectionUri を
    // 持つ既存データ・既存テストは 1 件も無かった（作業仕様書 §設計 (b)）。
    [Theory]
    [InlineData("postgresql://svc:{0}@db.example.test/kb")]
    [InlineData("Host=db.example.test;Password={0}")]
    public async Task Post_RejectsCredentialBearingConnectionUri(string template)
    {
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/datasources", new
        {
            name = "with-creds",
            sourceType = "db",
            connectionUri = string.Format(template, LeakedPassword),
            config = new Dictionary<string, string>(),
            defaultAttributes = new Dictionary<string, string>(),
        }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "connectionUri に資格情報を置かせない（DatabaseConnector の契約が既にそう定めていた）");
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("config", "どこへ移せばよいかまで案内する");
    }

    [Fact]
    public async Task Put_RejectsCredentialBearingConnectionUri()
    {
        var client = factory.CreateClient();
        var id = await CreateAsync(client, new Dictionary<string, string>());

        var resp = await client.PutAsJsonAsync($"/datasources/{id}", new
        {
            name = "n",
            sourceType = "db",
            connectionUri = $"postgresql://svc:{LeakedPassword}@db.example.test/kb",
            config = new Dictionary<string, string>(),
            defaultAttributes = new Dictionary<string, string>(),
        }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // 🔴 **応答のマスク済みの値をそのまま書き戻しても、保存された実値を壊さない。**
    // これが無いと、資格情報つきの既存行は「名前を 1 つ直す」だけで資格情報を失う。
    [Fact]
    public async Task Put_WritingBackMaskedConnectionUri_PreservesStoredCredential()
    {
        var client = factory.CreateClient();
        var real = $"postgresql://svc-account:{LeakedPassword}@db.example.test/kb";
        var id = SeedLegacyRow(real);

        // GET が返す（マスク済みの）値をそのまま送り返す。
        using var got = JsonDocument.Parse(await GetRawAsync(client, id));
        var masked = got.RootElement.GetProperty("connectionUri").GetString()!;

        var resp = await client.PutAsJsonAsync($"/datasources/{id}", new
        {
            name = "renamed",
            sourceType = "db",
            connectionUri = masked,
            config = new Dictionary<string, string>(),
            defaultAttributes = new Dictionary<string, string>(),
        }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "無変更の書き戻しは受理する（弾くと既存行を直せない）");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.DataSourceDbContext>();
        var stored = await db.DataSources.FindAsync([id], TestContext.Current.CancellationToken);
        stored!.ConnectionUri.Should().Be(real, "マスク値の書き戻しで実値を壊さない");
        stored.Name.Should().Be("renamed", "他項目の更新は通る");
    }

    // 🔴 **マスク値を「編集して」送り返す形は、どのマスク規則にも掛からない**
    // （`***` に `:` が無いので URI 規則が当たらない）。ここで止めないと、そのまま保存されて
    // **資格情報が黙って消える。**
    [Fact]
    public async Task Put_EditedMaskedConnectionUri_IsRejected_NotSilentlyStored()
    {
        var client = factory.CreateClient();
        var real = $"postgresql://svc-account:{LeakedPassword}@db.example.test/kb";
        var id = SeedLegacyRow(real);

        var resp = await client.PutAsJsonAsync($"/datasources/{id}", new
        {
            name = "n",
            sourceType = "db",
            // マスク済みの値のホスト名だけを直した形。
            connectionUri = "postgresql://***@db-new.example.test/kb",
            config = new Dictionary<string, string>(),
            defaultAttributes = new Dictionary<string, string>(),
        }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "黙って保存して資格情報を失わせない");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.DataSourceDbContext>();
        var stored = await db.DataSources.FindAsync([id], TestContext.Current.CancellationToken);
        stored!.ConnectionUri.Should().Be(real, "拒否した以上、保存値は 1 バイトも変わらない");
    }

    // 資格情報を持たない普通の接続先は従来どおり通る（過剰に弾かない）。
    [Theory]
    [InlineData("smb://share/docs")]
    [InlineData("https://wiki.example.com")]
    [InlineData("Host=db.example.test;Username=app;Database=kb")]
    [InlineData("")]
    public async Task Post_AcceptsConnectionUriWithoutCredentials(string uri)
    {
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/datasources", new
        {
            name = "plain",
            sourceType = "filesystem",
            connectionUri = uri,
            config = new Dictionary<string, string>(),
            defaultAttributes = new Dictionary<string, string>(),
        }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Created, "資格情報を持たない接続先は従来どおり通す");
    }

    // ---- (a) 手動同期 API の応答 -------------------------------------------------------

    // 🔴 **同じ例外が `AlertOnFailure` 経由ではマスクして保存されるのに、応答だけ素通しだった。**
    // `DatabaseConnector` は `builder["Password"]` で接続文字列を合成して `OpenAsync` するため、
    // Npgsql の接続失敗例外にパスワードが載る経路が実在する。
    [Fact]
    public async Task Sync_DiscoverFailure_ResponseMessageIsRedacted()
    {
        var client = LeakingConnectorClient();
        var id = SeedLegacyRow("smb://share/docs", sourceType: LeakingConnector.Type);

        var resp = await client.PostAsync($"/datasources/{id}/sync", content: null,
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain(LeakedPassword, "手動同期の応答に平文の資格情報を載せない");
        body.Should().Contain("discover failed", "原因の手掛かりは残す（過剰マスクにしない）");
        body.Should().Contain("Password=***", "キー名は残し値だけを伏せる");
    }

    // 保存側（既存の守り）が退行していないこと。応答を直したついでに壊していないかを見る。
    [Fact]
    public async Task Sync_DiscoverFailure_StoredErrorIsAlsoRedacted()
    {
        var client = LeakingConnectorClient();
        var id = SeedLegacyRow("smb://share/docs", sourceType: LeakingConnector.Type);

        await client.PostAsync($"/datasources/{id}/sync", content: null, TestContext.Current.CancellationToken);

        var body = await GetRawAsync(client, id);
        body.Should().NotContain(LeakedPassword, "保存された直近エラーにも平文の資格情報を残さない");
    }

    // ---- (d) 例外がそのままログへ落ちる -----------------------------------------------

    // 🔴 **`ex` を第 1 引数に渡すと `ILogger` は `Exception.ToString()` を LogRecord へ載せる。**
    // これはメッセージ＋内部例外のメッセージ＋スタックであり、共通ログ基盤にスクラビングが無い以上
    // （`Foundation/` を `redact|scrub|sanitiz|mask` で走査して 0 件）そのまま外へ出る。
    //
    // **2 つを同時に見る。** 整形済みメッセージだけを見ると、例外オブジェクト経由の間接出力を見逃す。
    [Fact]
    public async Task Sync_DiscoverFailure_DoesNotPassExceptionObjectToLogger()
    {
        var log = new RecordingLogger();
        var svc = BuildSyncService(new LeakingConnector(), log);
        var source = DataSource.Create("leaky", LeakingConnector.Type, "smb://share/docs");

        await svc.SyncAsync(source, TestContext.Current.CancellationToken);

        log.Records.Should().NotBeEmpty("失敗は記録される（黙って捨てない）");
        log.Records.Should().OnlyContain(r => r.Exception == null,
            "例外オブジェクトを渡すと Exception.ToString() がログレコードへ入る");
        log.Records.Should().OnlyContain(r => !r.Message.Contains(LeakedPassword),
            "整形済みメッセージにも平文の資格情報を載せない");
        log.Records.Should().Contain(r => r.Message.Contains("System.IO.IOException"),
            "例外の型名は残す（資格情報を運ばず、切り分けの主要な手掛かりである）");
    }

    // fetch 側の同型。**応答には載らないがログには載っていた**（`RunAsync` は fetch 失敗時に
    // `Message: null` を返すため、素通しだったのはログだけである）。
    [Fact]
    public async Task Sync_FetchFailure_DoesNotPassExceptionObjectToLogger()
    {
        var log = new RecordingLogger();
        var svc = BuildSyncService(new LeakingFetchConnector(), log);
        var source = DataSource.Create("leaky-fetch", LeakingFetchConnector.Type, "smb://share/docs");

        var result = await svc.SyncAsync(source, TestContext.Current.CancellationToken);

        result.Failed.Should().Be(1);
        log.Records.Should().NotBeEmpty();
        log.Records.Should().OnlyContain(r => r.Exception == null);
        log.Records.Should().OnlyContain(r => !r.Message.Contains(LeakedPassword),
            "fetch 失敗のログにも平文の資格情報を載せない");
    }

    // ---- helpers ----------------------------------------------------------------------

    private async Task<Guid> CreateAsync(HttpClient client, Dictionary<string, string> config)
    {
        var resp = await client.PostAsJsonAsync("/datasources", new
        {
            name = "exposure-" + Guid.NewGuid().ToString("N")[..8],
            sourceType = "wiki",
            connectionUri = "https://wiki.example.com",
            config,
            defaultAttributes = new Dictionary<string, string>(),
        }, TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return doc.GetProperty("id").GetGuid();
    }

    private static async Task<string> GetRawAsync(HttpClient client, Guid id)
    {
        var resp = await client.GetAsync($"/datasources/{id}", TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    // **API を経由せずに行を作る。** 資格情報つきの connectionUri は API が 400 で弾くようになったので、
    // 「本対応より前から在る行」を模すにはこれしか手が無い —— そして**その行こそが本作業の守る対象**である。
    private Guid SeedLegacyRow(string connectionUri, string sourceType = "db")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.DataSourceDbContext>();
        var ds = DataSource.Create("legacy-" + Guid.NewGuid().ToString("N")[..8], sourceType, connectionUri);
        db.DataSources.Add(ds);
        db.SaveChanges();
        return ds.Id;
    }

    // 秘密を運ぶ例外を投げるコネクタを DI へ足したクライアント（実コネクタは資格情報を
    // 決定的には漏らさないため、**陽性対照はスタブで作る**）。
    private HttpClient LeakingConnectorClient()
        => factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IDataSourceConnector, LeakingConnector>();
            s.AddSingleton<IDataSourceConnector, LeakingFetchConnector>();
        })).CreateClient();

    private DataSourceSyncService BuildSyncService(IDataSourceConnector connector, ILogger<DataSourceSyncService> log)
    {
        using var scope = factory.Services.CreateScope();
        return new DataSourceSyncService(
            new ConnectorRegistry([connector]),
            scope.ServiceProvider.GetRequiredService<IObjectStorageClient>(),
            scope.ServiceProvider.GetRequiredService<RecordingMessageBus>(),
            log);
    }

    // discover が資格情報つきの例外を投げるコネクタ。`DatabaseConnector` が
    // `builder["Password"]` で合成した接続文字列で `OpenAsync` して落ちる形を模す。
    private sealed class LeakingConnector : IDataSourceConnector
    {
        public const string Type = "leaky-discover";
        public string SourceType => Type;
        public Task<IReadOnlyList<SourceItem>> DiscoverAsync(DataSource s, DateTimeOffset? since, CancellationToken ct)
            => throw new IOException(
                $"connection failed: Host=db.example.test;Username=app;Password={LeakedPassword};Database=kb");
        public Task<RawContent> FetchAsync(DataSource s, SourceItem item, CancellationToken ct)
            => throw new NotSupportedException();
    }

    // discover は成功し fetch が資格情報つきの例外を投げるコネクタ。
    private sealed class LeakingFetchConnector : IDataSourceConnector
    {
        public const string Type = "leaky-fetch";
        public string SourceType => Type;
        public Task<IReadOnlyList<SourceItem>> DiscoverAsync(DataSource s, DateTimeOffset? since, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SourceItem>>([new SourceItem("/x/a.md", DateTimeOffset.UtcNow, 1)]);
        public Task<RawContent> FetchAsync(DataSource s, SourceItem item, CancellationToken ct)
            => throw new IOException(
                $"fetch failed: https://svc-account:{LeakedPassword}@saas.example.test/api/items/1");
    }

    // ログレコードを捕捉する。**例外オブジェクトと整形済みメッセージの両方**を保持する ——
    // 片方だけだと `ex` 経由の間接出力を見逃す（それが本経路の欠陥そのものだった）。
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
