using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using DataSourceService.Domain;
using DataSourceService.Infrastructure.Persistence;
using DataSourceService.Infrastructure.Secrets;
using DataSourceService.Infrastructure.Secrets.Migration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace DataSourceService.Tests.Infrastructure.Secrets.Migration;

// NFR-18, FR-01, 計画 ADR-0126 フォローアップ 2, [[IADR-0663]] 決定 3・4 (#458 段 S4): 平文の資格情報の**移送**。
// Vault は偽のハンドラ（KV v2 の data の GET / POST・metadata の GET）、DB は InMemory（条件つき更新の実 SQL は
// Knowledge.IntegrationTests の CredentialMigrationStoreTests が実 PostgreSQL で測る）。
[Trait("TestKind", "Unit")]
public sealed class ConnectorCredentialMigratorTests
{
    private const string Plain = "plaintext-migration-marker-4b7e";
    private const string Token = "hvs.migration-token-marker";

    private sealed class StubKv : HttpMessageHandler
    {
        public Dictionary<string, (int Version, Dictionary<string, string> Data)> Store { get; } = new(StringComparer.Ordinal);
        public List<(string Method, string Path, string? Body, string? Token)> Requests { get; } = [];
        public HttpStatusCode? FailWritesWith { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method.Method, path, body, request.Headers.TryGetValues("X-Vault-Token", out var t) ? t.Single() : null));
            var key = path.Replace("/v1/secret/data/", "").Replace("/v1/secret/metadata/", "");
            if (request.Method == HttpMethod.Get && path.StartsWith("/v1/secret/metadata/", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Get)
            {
                if (!Store.TryGetValue(key, out var s))
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                return Json(new { data = new { data = s.Data, metadata = new { version = s.Version } } });
            }

            if (FailWritesWith is { } fail)
                return new HttpResponseMessage(fail);
            using var doc = JsonDocument.Parse(body!);
            var cas = doc.RootElement.GetProperty("options").GetProperty("cas").GetInt32();
            var current = Store.TryGetValue(key, out var existing) ? existing.Version : 0;
            if (cas != current)
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            var data = doc.RootElement.GetProperty("data").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
            Store[key] = (current + 1, data);
            return Json(new { data = new { version = current + 1 } });
        }

        private static HttpResponseMessage Json(object o) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " EXC"));
    }

    private static DataSourceDbContext NewDb() =>
        new(new DbContextOptionsBuilder<DataSourceDbContext>().UseInMemoryDatabase($"migrate-{Guid.NewGuid()}").Options);

    private static async Task<DataSource> AddAsync(DataSourceDbContext db, string type, Dictionary<string, string> config)
    {
        var ds = DataSource.Create($"{type}-{Guid.NewGuid():N}", type, "https://source.example.test", config);
        db.DataSources.Add(ds);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return ds;
    }

    private static (ConnectorCredentialMigrator Migrator, StubKv Kv, RecordingLogger<ConnectorCredentialMigrator> Log) Build(DataSourceDbContext db)
    {
        var kv = new StubKv();
        var http = new HttpClient(kv) { BaseAddress = new Uri("http://vault.test:8200/") };
        var log = new RecordingLogger<ConnectorCredentialMigrator>();
        return (new ConnectorCredentialMigrator(db, new MigrationVaultKvClient(http, Token, "secret"), log), kv, log);
    }

    private static string StoredValue(DataSourceDbContext db, Guid id, string key) =>
        db.DataSources.AsNoTracking().Single(d => d.Id == id).Config[key];

    [Fact]
    public async Task Plaintext_is_written_to_vault_first_then_replaced_by_the_canonical_reference()
    {
        using var db = NewDb();
        var ds = await AddAsync(db, "wiki", new() { ["apiToken"] = Plain, ["spaceKey"] = "DOCS" });
        var (migrator, kv, log) = Build(db);

        var report = await migrator.RunAsync(dryRun: false, TestContext.Current.CancellationToken);

        report.Should().Be(new ConnectorCredentialMigrator.Report(1, 1, 1, 0, 0, false));
        kv.Store[$"datasource/{ds.Id:D}"].Data.Should().Equal(new Dictionary<string, string> { ["apiToken"] = Plain });
        StoredValue(db, ds.Id, "apiToken").Should().Be(ConnectorSecretReference.CanonicalFor(ds.Id, "apiToken"));
        StoredValue(db, ds.Id, "spaceKey").Should().Be("DOCS", "秘密でないキーは触らない");
        kv.Requests.Should().AllSatisfy(r => r.Token.Should().Be(Token));
        kv.Requests.Select(r => r.Method).Should().Equal(["GET", "GET", "POST", "GET"], "読む（data・metadata）→ 書く → 読み直す");
        string.Join("\n", log.Lines).Should().NotContain(Plain).And.NotContain(ds.Id.ToString("D")).And.NotContain("apiToken")
            .And.NotContain("datasource/").And.NotContain(Token).And.NotContain("EXC");
    }

    [Fact]
    public async Task Rerunning_is_a_no_op_and_the_startup_gate_counts_zero()
    {
        using var db = NewDb();
        await AddAsync(db, "db", new() { ["password"] = Plain });
        var (migrator, kv, _) = Build(db);
        await migrator.RunAsync(dryRun: false, TestContext.Current.CancellationToken);
        var writes = kv.Requests.Count(r => r.Method == "POST");

        var second = await migrator.RunAsync(dryRun: false, TestContext.Current.CancellationToken);

        second.Should().Be(new ConnectorCredentialMigrator.Report(0, 0, 0, 0, 0, false));
        kv.Requests.Count(r => r.Method == "POST").Should().Be(writes, "2 回目は何も書かない（冪等）");
        (await PlaintextCredentialStartupGate.CountPlaintextRowsAsync(db, TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task Other_keys_at_the_same_vault_path_are_kept_and_the_write_uses_check_and_set()
    {
        using var db = NewDb();
        var ds = await AddAsync(db, "saas", new() { ["apiToken"] = Plain });
        var (migrator, kv, _) = Build(db);
        kv.Store[$"datasource/{ds.Id:D}"] = (3, new() { ["otherKey"] = "kept-value" });

        await migrator.RunAsync(dryRun: false, TestContext.Current.CancellationToken);

        kv.Store[$"datasource/{ds.Id:D}"].Should().BeEquivalentTo((4, new Dictionary<string, string> { ["otherKey"] = "kept-value", ["apiToken"] = Plain }));
        using var body = JsonDocument.Parse(kv.Requests.Single(r => r.Method == "POST").Body!);
        body.RootElement.GetProperty("options").GetProperty("cas").GetInt32().Should().Be(3, "読んだ版で書く");
    }

    [Fact]
    public async Task Same_value_already_in_vault_skips_the_write_and_still_places_the_reference()
    {
        using var db = NewDb();
        var ds = await AddAsync(db, "wiki", new() { ["apiToken"] = Plain });
        var (migrator, kv, _) = Build(db);
        kv.Store[$"datasource/{ds.Id:D}"] = (1, new() { ["apiToken"] = Plain });

        var report = await migrator.RunAsync(dryRun: false, TestContext.Current.CancellationToken);

        report.Migrated.Should().Be(1);
        kv.Requests.Should().NotContain(r => r.Method == "POST", "途中で落ちた後の再開: 同じ値は書き直さない");
        StoredValue(db, ds.Id, "apiToken").Should().Be(ConnectorSecretReference.CanonicalFor(ds.Id, "apiToken"));
    }

    [Fact]
    public async Task Different_value_in_vault_is_a_conflict_and_leaves_the_row_untouched()
    {
        using var db = NewDb();
        var ds = await AddAsync(db, "wiki", new() { ["apiToken"] = Plain });
        var (migrator, kv, _) = Build(db);
        kv.Store[$"datasource/{ds.Id:D}"] = (2, new() { ["apiToken"] = "written-from-sc22" });

        var report = await migrator.RunAsync(dryRun: false, TestContext.Current.CancellationToken);

        report.Conflicts.Should().Be(1);
        report.Succeeded.Should().BeFalse();
        kv.Store[$"datasource/{ds.Id:D}"].Data["apiToken"].Should().Be("written-from-sc22", "SC-22 から書いた値を平文で上書きしない");
        StoredValue(db, ds.Id, "apiToken").Should().Be(Plain, "衝突した行の設定は触らない");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Vault_write_failure_leaves_the_plaintext_in_place(HttpStatusCode status)
    {
        using var db = NewDb();
        var ds = await AddAsync(db, "db", new() { ["password"] = Plain });
        var (migrator, kv, log) = Build(db);
        kv.FailWritesWith = status;

        var report = await migrator.RunAsync(dryRun: false, TestContext.Current.CancellationToken);

        report.Failed.Should().Be(1);
        StoredValue(db, ds.Id, "password").Should().Be(Plain, "Vault が先・設定が後。Vault に書けなければ設定を触らない");
        string.Join("\n", log.Lines).Should().Contain(((int)status).ToString()).And.NotContain(Plain).And.NotContain("EXC");
    }

    [Fact]
    public async Task Dry_run_counts_without_touching_vault_or_the_rows()
    {
        using var db = NewDb();
        var a = await AddAsync(db, "wiki", new() { ["apiToken"] = Plain });
        await AddAsync(db, "db", new() { ["password"] = Plain, ["apiKey"] = Plain });
        await AddAsync(db, "wiki", new() { ["apiToken"] = "vault:datasource/elsewhere#apiToken" });
        await AddAsync(db, "filesystem", new() { ["rootPath"] = "/share" });
        var (migrator, kv, _) = Build(db);

        var report = await migrator.RunAsync(dryRun: true, TestContext.Current.CancellationToken);

        report.Should().Be(new ConnectorCredentialMigrator.Report(2, 3, 0, 0, 0, true));
        kv.Requests.Should().BeEmpty();
        StoredValue(db, a.Id, "apiToken").Should().Be(Plain);
    }

    [Fact]
    public async Task Disabled_rows_are_migrated_too()
    {
        using var db = NewDb();
        var ds = await AddAsync(db, "wiki", new() { ["apiToken"] = Plain });
        var tracked = db.DataSources.Single(d => d.Id == ds.Id);
        tracked.Disable();
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var (migrator, _, _) = Build(db);

        await migrator.RunAsync(dryRun: false, TestContext.Current.CancellationToken);

        StoredValue(db, ds.Id, "apiToken").Should().Be(ConnectorSecretReference.CanonicalFor(ds.Id, "apiToken"),
            "無効化した行の平文も保管の対象である");
    }

    // ── 入口（使い方・構成の誤りは 2）

    [Theory]
    [InlineData]
    [InlineData("--dry-run", "--token-stdin")]
    [InlineData("--token-stdin", "--unknown")]
    public async Task Command_rejects_ambiguous_arguments_with_exit_2(params string[] rest)
    {
        using var factory = new TestWebApplicationFactory();
        var stdout = new StringWriter();
        var code = await ConnectorCredentialMigrationCommand.RunAsync(
            factory.Services, [ConnectorCredentialMigrationCommand.Verb, .. rest], new StringReader(Token), stdout,
            TestContext.Current.CancellationToken);
        code.Should().Be(2);
        stdout.ToString().Should().Contain("使い方");
    }

    [Fact]
    public async Task Command_refuses_to_migrate_without_a_vault_address()
    {
        using var factory = new TestWebApplicationFactory();
        var stdout = new StringWriter();
        var code = await ConnectorCredentialMigrationCommand.RunAsync(
            factory.Services, [ConnectorCredentialMigrationCommand.Verb, "--token-stdin"], new StringReader(Token), stdout,
            TestContext.Current.CancellationToken);
        code.Should().Be(2);
        stdout.ToString().Should().Contain("Vault:Address").And.NotContain(Token);
    }

    // コーディネーターの条件 (1)（2026-10-31）: 標準入力で受けたトークンと秘密の値を、出力・例外・終了コードの説明に出さない。
    // 保管先に届かない構成（不達）で移送を流し、失敗の経路（例外を捕まえて数える経路）を通す。
    [Fact]
    public async Task Command_never_prints_the_token_or_the_secret_even_when_vault_is_unreachable()
    {
        using var baseFactory = new TestWebApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(b =>
            b.UseSetting("Vault:Address", "http://127.0.0.1:9"));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DataSourceDbContext>();
            await AddAsync(db, "wiki", new() { ["apiToken"] = Plain });
        }

        var stdout = new StringWriter();
        var code = await ConnectorCredentialMigrationCommand.RunAsync(
            factory.Services, [ConnectorCredentialMigrationCommand.Verb, "--token-stdin"], new StringReader(Token + "\n"), stdout,
            TestContext.Current.CancellationToken);

        code.Should().Be(1, "届かなかった行は失敗として数え、例外で落ちない");
        stdout.ToString().Should().Contain("失敗 1").And.Contain("平文が残る行 1")
            .And.NotContain(Token).And.NotContain(Plain).And.NotContain("datasource/");
        using var check = factory.Services.CreateScope();
        check.ServiceProvider.GetRequiredService<DataSourceDbContext>().DataSources.AsNoTracking().Single().Config["apiToken"]
            .Should().Be(Plain, "保管先に書けなかった行の設定は触らない");
    }

    [Fact]
    public async Task Command_dry_run_reports_counts_and_exits_0()
    {
        using var factory = new TestWebApplicationFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DataSourceDbContext>();
            await AddAsync(db, "wiki", new() { ["apiToken"] = Plain });
        }

        var stdout = new StringWriter();
        var code = await ConnectorCredentialMigrationCommand.RunAsync(
            factory.Services, [ConnectorCredentialMigrationCommand.Verb, "--dry-run"], new StringReader(string.Empty), stdout,
            TestContext.Current.CancellationToken);

        code.Should().Be(0);
        stdout.ToString().Should().Contain("平文を持つ行 1").And.Contain("平文が残る行 1").And.NotContain(Plain);
    }
}
