using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using McpServer.Domain;
using McpServer.Domain.Ports;
using McpServer.Features.McpClients.IdpReconciliation;
using McpServer.Infrastructure.ExternalServices;
using McpServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static McpServer.Tests.Infrastructure.ExternalServices.KeycloakServiceAccountProvisionerTests;

namespace McpServer.Tests.Features.McpClients.IdpReconciliation;

// FR-16, SC-12, 計画 ADR-0123 決定 2・フォローアップ 2, [[IADR-0516]] 決定 5（2026-10-09 追記 / #1818）:
// **登録簿と IdP のサービスアカウントの属性の食い違いを、定期の照合で検知して知らせる**（issue #1818 の受け入れ基準 1〜5）。
// 知らせる経路は計器 → 警報（`McpClientIdpDrift` / `McpClientIdpReconciliationSeriesAbsent`）と Warning / Error ログである。
// IdP はプロセス内の口（`InMemoryServiceAccountProvisioner`。書き込み口と同じ意味論）か、読み取りを差し替えた偽物で扱う。
[Trait("TestKind", "Unit")]
public class IdpReconciliationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static McpDbContext NewDb(string? name = null)
        => new(new DbContextOptionsBuilder<McpDbContext>()
            .UseInMemoryDatabase(name ?? $"IdpReconciliation_{Guid.NewGuid()}").Options);

    private static Dictionary<string, string> Attrs(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    private static readonly IdpReconciliationOptions Options = new(IdpReconciliationOptions.DefaultInterval);

    // 計器は自分の IMeterFactory の器だけを聞く（同じ Meter 名の他の試験の測定を混ぜない。IADR-0481 の試験と同じ形）。
    internal sealed class Probe : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<string, long> _outcomes = new();
        private readonly List<int> _gauge = [];

        public Probe(IMeterFactory factory)
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, factory)
                    && instrument.Meter.Name == IdpReconciliationMetrics.MeterName
                    && instrument.Name is IdpReconciliationMetrics.DriftedGaugeName or IdpReconciliationMetrics.CheckCounterName)
                    l.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                    if (tag.Key == IdpReconciliationMetrics.OutcomeTag)
                        _outcomes.AddOrUpdate((string)tag.Value!, value, (_, v) => v + value);
            });
            _listener.SetMeasurementEventCallback<int>((_, value, tags, _) =>
            {
                tags.Length.Should().Be(0, "ゲージに属性（クライアント ID 等）を載せない＝系列の数は 1 つ");
                lock (_gauge) _gauge.Add(value);
            });
            _listener.Start();
        }

        public long Outcome(string outcome) => _outcomes.GetValueOrDefault(outcome);

        public IReadOnlyList<int> CollectGauge()
        {
            lock (_gauge) _gauge.Clear();
            _listener.RecordObservableInstruments();
            lock (_gauge) return [.. _gauge];
        }

        public void Dispose() => _listener.Dispose();
    }

    internal sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Enqueue((logLevel, formatter(state, exception)));
        public int Count(LogLevel level) => Entries.Count(e => e.Level == level);
    }

    internal sealed record Arranged(
        IdpReconciliationCheck Check, IdpReconciliationMetrics Metrics, Probe Probe, RecordingLogger<IdpReconciliationCheck> Log);

    internal static Arranged Arrange(McpDbContext db, IServiceAccountDirectory directory, TimeProvider? clock = null)
    {
        var factory = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
        var probe = new Probe(factory);
        var metrics = new IdpReconciliationMetrics(factory);
        var log = new RecordingLogger<IdpReconciliationCheck>();
        return new Arranged(
            new IdpReconciliationCheck(db, directory, metrics, Options, clock ?? TimeProvider.System, log), metrics, probe, log);
    }

    // 登録簿へ行を置く（入口を通さない＝照合の前提を直に作る）。
    private static async Task AddRow(McpDbContext db, string clientId, IReadOnlyDictionary<string, string> attributes,
        McpClientKind kind = McpClientKind.ServiceAccount)
    {
        db.Clients.Add(McpClient.Register(clientId, clientId, kind, attributes, EgressTier.StandardExternal, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(Ct);
    }

    // 入口の書き込みと同じ形で、IdP と登録簿の両方へ同じ値を置く。
    private static async Task Provision(McpDbContext db, InMemoryServiceAccountProvisioner idp, string clientId,
        IReadOnlyDictionary<string, string> attributes)
    {
        await idp.CreateAsync(clientId, clientId, attributes, Ct);
        await AddRow(db, clientId, attributes);
    }

    // C-41（陽性対照）: 一致していればゲージは 0、結末は match、食い違いの Warning を出さない。集合値の順序は食い違いにしない。
    [Fact]
    public async Task 一致していればゲージは0で結末はmatch()
    {
        using var db = NewDb();
        var idp = new InMemoryServiceAccountProvisioner();
        await Provision(db, idp, "agent-a", Attrs(("clearance", "internal"), ("projects", "b,a")));
        idp.Tamper("agent-a", Attrs(("clearance", "internal"), ("projects", "a, b")));
        // 有人の行は IdP へ書かない（既知の逸脱）ので比べない。
        await AddRow(db, "human-ui", Attrs(("clearance", "secret")), McpClientKind.Interactive);
        var (check, _, probe, log) = Arrange(db, idp);
        using var _p = probe;

        (await check.RunAsync(Ct)).Should().BeEmpty();

        probe.CollectGauge().Should().Equal(0);
        probe.Outcome(IdpReconciliationMetrics.OutcomeMatch).Should().Be(1);
        probe.Outcome(IdpReconciliationMetrics.OutcomeDrift).Should().Be(0);
        log.Count(LogLevel.Warning).Should().Be(0);
        log.Count(LogLevel.Error).Should().Be(0);
    }

    public static TheoryData<string, IdpDriftKind> Kinds() => new()
    {
        { "IdP にクライアントが無い", IdpDriftKind.ClientMissing },
        { "入口の印が無い", IdpDriftKind.NotManaged },
        { "サービスアカウントが無い", IdpDriftKind.ServiceAccountMissing },
        { "属性が違う", IdpDriftKind.AttributesDiffer },
        { "印つきのクライアントに登録簿の行が無い（孤児）", IdpDriftKind.Orphan },
        { "有効・無効が違う（#1829）", IdpDriftKind.EnabledDiffers },
    };

    // C-42（受け入れ基準 1・2）: 種類ごとに、ゲージが 1・結末が drift・行を名指しする Warning（クライアント ID と種類）。
    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task 食い違いは種類ごとに検知しゲージとカウンタとログへ出す(string _, IdpDriftKind kind)
    {
        using var db = NewDb();
        var idp = new InMemoryServiceAccountProvisioner();
        await Provision(db, idp, "agent-ok", Attrs(("clearance", "public")));
        IServiceAccountDirectory directory = idp;
        switch (kind)
        {
            case IdpDriftKind.ClientMissing:
                await AddRow(db, "agent-x", Attrs(("clearance", "public")));
                break;
            case IdpDriftKind.NotManaged:
                idp.Seed("agent-x", Attrs(("clearance", "public")));
                await AddRow(db, "agent-x", Attrs(("clearance", "public")));
                break;
            case IdpDriftKind.ServiceAccountMissing:
                await Provision(db, idp, "agent-x", Attrs(("clearance", "public")));
                directory = new HidingDirectory(idp, "agent-x");
                break;
            case IdpDriftKind.AttributesDiffer:
                await Provision(db, idp, "agent-x", Attrs(("clearance", "public")));
                idp.Tamper("agent-x", Attrs(("clearance", "secret")));
                break;
            case IdpDriftKind.Orphan:
                idp.SeedManaged("agent-x", Attrs(("clearance", "public")));
                break;
            case IdpDriftKind.EnabledDiffers:
                // IdP の管理画面での直接の無効化（登録簿は有効のまま）。
                await Provision(db, idp, "agent-x", Attrs(("clearance", "public")));
                idp.TamperEnabled("agent-x", false);
                break;
        }
        var (check, _, probe, log) = Arrange(db, directory);
        using var _p = probe;

        var drifts = await check.RunAsync(Ct);

        drifts.Should().ContainSingle().Which.Should().Be(new IdpDrift("agent-x", kind));
        probe.CollectGauge().Should().Equal(1);
        probe.Outcome(IdpReconciliationMetrics.OutcomeDrift).Should().Be(1);
        probe.Outcome(IdpReconciliationMetrics.OutcomeMatch).Should().Be(0);
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Contain($"client=agent-x kind={drifts![0].KindLabel}");
    }

    // C-50（#1829 受け入れ基準 3・IADR-0516 決定 4a）: 無効化の IdP への写しが失敗した形（登録簿は無効・IdP は有効）を
    // `enabled_differs` として拾う。1 つのクライアントが属性違いと有効・無効違いを同時に持っても、ゲージは**クライアントの数**で 1。
    // 両方が無効なら食い違いではない（陽性対照）。
    [Fact]
    public async Task 有効無効の食い違いを拾いゲージはクライアントの数で数える()
    {
        using var db = NewDb();
        var idp = new InMemoryServiceAccountProvisioner();
        await Provision(db, idp, "agent-disabled-ok", Attrs(("clearance", "public")));
        await Provision(db, idp, "agent-mirror-failed", Attrs(("clearance", "public")));
        await Provision(db, idp, "agent-both", Attrs(("clearance", "public")));
        foreach (var row in db.Clients) row.SetEnabled(row.ClientId == "agent-both", DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(Ct);
        idp.TamperEnabled("agent-disabled-ok", false);          // 両方とも無効 → 一致
        // agent-mirror-failed: 登録簿は無効・IdP は有効のまま（写しの失敗）
        idp.TamperEnabled("agent-both", false);                 // 登録簿は有効・IdP は無効
        idp.Tamper("agent-both", Attrs(("clearance", "secret"))); // ＋ 属性も違う
        var (check, _, probe, log) = Arrange(db, idp);
        using var _p = probe;

        var drifts = await check.RunAsync(Ct);

        drifts.Should().BeEquivalentTo(new[]
        {
            new IdpDrift("agent-both", IdpDriftKind.AttributesDiffer),
            new IdpDrift("agent-both", IdpDriftKind.EnabledDiffers),
            new IdpDrift("agent-mirror-failed", IdpDriftKind.EnabledDiffers),
        }, o => o.WithStrictOrdering());
        probe.CollectGauge().Should().Equal(2);
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information && e.Message.Contains("食い違い 2 件"))
            .Which.Message.Should().Contain("attributes_differ=1 orphan=0 enabled_differs=2");
        log.Entries.Should().Contain(e => e.Message.Contains("client=agent-mirror-failed kind=enabled_differs"));
    }

    // C-50（#1829）: 名指しの重大度は 属性違い → 孤児 → 有効・無効違い → 印なし → SA なし → IdP に無い。
    [Fact]
    public void 有効無効の違いは孤児の次で印なしより先に名指しする()
    {
        Enum.GetValues<IdpDriftKind>().Select(k => new IdpDrift("x", k)).OrderBy(d => d.Severity).Select(d => d.KindLabel)
            .Should().Equal("attributes_differ", "orphan", "enabled_differs", "not_managed", "service_account_missing", "client_missing");
    }

    // C-43（受け入れ基準 3）: 未照合はゲージの系列を出さない。照合に失敗したら系列を止め（前の値を残さない）、結末は failed・Error を出す。
    [Fact]
    public async Task 未照合と失敗はゲージの系列を出さずfailedを数える()
    {
        using var db = NewDb();
        var idp = new InMemoryServiceAccountProvisioner();
        await Provision(db, idp, "agent-a", Attrs(("clearance", "public")));
        idp.Tamper("agent-a", Attrs(("clearance", "secret")));
        var failing = new FailingDirectory(idp);
        var (check, _, probe, log) = Arrange(db, failing);
        using var _p = probe;

        probe.CollectGauge().Should().BeEmpty("まだ照合していない");

        (await check.RunAsync(Ct)).Should().HaveCount(1);
        probe.CollectGauge().Should().Equal(1);

        failing.Fail = true;
        (await check.RunAsync(Ct)).Should().BeNull();

        probe.CollectGauge().Should().BeEmpty("失敗したら前の値（1）を出し続けない＝消えたことを隠さない");
        probe.Outcome(IdpReconciliationMetrics.OutcomeFailed).Should().Be(1);
        log.Count(LogLevel.Error).Should().Be(1);
    }

    // C-43: 書き込み口が構成されていなければ照合は失敗として数える（系列なし）。理由は Warning（Error で埋めない）。
    [Fact]
    public async Task 口が構成されていなければ失敗として数える()
    {
        using var db = NewDb();
        await AddRow(db, "agent-a", Attrs(("clearance", "public")));
        var (check, _, probe, log) = Arrange(db, new UnconfiguredServiceAccountProvisioner());
        using var _p = probe;

        (await check.RunAsync(Ct)).Should().BeNull();

        probe.CollectGauge().Should().BeEmpty();
        probe.Outcome(IdpReconciliationMetrics.OutcomeFailed).Should().Be(1);
        log.Count(LogLevel.Warning).Should().Be(1);
        log.Count(LogLevel.Error).Should().Be(0);
    }

    // C-44（受け入れ基準 4・否定形）: 照合は IdP にも登録簿にも書かない。偽の Keycloak が受けた管理要求はすべて GET、
    // 登録簿の行（属性・更新時刻）は変わらない。食い違いを見つけても直さない。
    [Fact]
    public async Task 照合はIdPにも登録簿にも書かない()
    {
        var keycloak = new FakeKeycloak();
        var directory = new KeycloakServiceAccountProvisioner(new StubFactory(keycloak), new ServiceAccountProvisioningOptions
        {
            BaseUrl = "https://auth.example.test",
            Realm = "platform",
            ClientId = "mcp-client-admin",
            ClientSecret = "injected-at-deploy-time",
        }, TimeProvider.System, NullLogger<KeycloakServiceAccountProvisioner>.Instance);
        await directory.CreateAsync("agent-a", "A", Attrs(("clearance", "secret")), Ct);
        keycloak.Requests.Clear();
        var name = $"IdpReconciliation_{Guid.NewGuid()}";
        using (var seed = NewDb(name))
        {
            await AddRow(seed, "agent-a", Attrs(("clearance", "public")));
            await AddRow(seed, "agent-gone", Attrs(("clearance", "public")));
        }
        using var db = NewDb(name);
        var before = await db.Clients.AsNoTracking().OrderBy(c => c.ClientId).ToListAsync(Ct);
        var (check, _, probe, _) = Arrange(db, directory);
        using var _p = probe;

        (await check.RunAsync(Ct)).Should().HaveCount(2);

        keycloak.Requests.Where(r => r.Path.StartsWith("admin/", StringComparison.Ordinal))
            .Should().NotBeEmpty().And.OnlyContain(r => r.Method == "GET", "照合は IdP へ書かない");
        keycloak.ServiceAccountOf("agent-a").Attributes["clearance"].Should().Equal(["secret"], "食い違いを直さない");
        db.ChangeTracker.Entries().Should().BeEmpty("登録簿は追跡なしで読む");
        using var after = NewDb(name);
        (await after.Clients.AsNoTracking().OrderBy(c => c.ClientId).ToListAsync(Ct))
            .Select(c => (c.ClientId, c.UpdatedAt, string.Join(";", c.Attributes.Select(kv => $"{kv.Key}={kv.Value}"))))
            .Should().Equal(before.Select(c => (c.ClientId, c.UpdatedAt, string.Join(";", c.Attributes.Select(kv => $"{kv.Key}={kv.Value}")))));
    }

    // C-45（受け入れ基準 5）: 1 回の照合は期限（周期と同じ長さ）を超えたら失敗として数える。停止要求の取り消しは失敗と数えず外へ出す。
    [Fact]
    public async Task 一回の照合は期限を超えたら失敗で停止要求は失敗と数えない()
    {
        using var db = NewDb();
        await AddRow(db, "agent-a", Attrs(("clearance", "public")));
        var clock = new FakeTimeProvider();
        var (check, _, probe, log) = Arrange(db, new HangingDirectory(), clock);
        using var _p = probe;

        var timedOut = check.RunAsync(Ct);
        clock.Advance(Options.CycleTimeout);
        // 見張り（実時間 10 秒）: 期限が効いていなければ、ここで時間切れの例外で落ちる（試験が止まらないことは無い）。
        (await timedOut.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Should().BeNull();
        probe.Outcome(IdpReconciliationMetrics.OutcomeFailed).Should().Be(1);
        log.Count(LogLevel.Error).Should().Be(1);

        using var stopping = new CancellationTokenSource();
        var stopped = check.RunAsync(stopping.Token);
        await stopping.CancelAsync();
        var act = () => stopped;
        await act.Should().ThrowAsync<OperationCanceledException>();
        probe.Outcome(IdpReconciliationMetrics.OutcomeFailed).Should().Be(1, "停止は照合の失敗ではない");
    }

    // C-45: 行ごとの IdP の読み取りは並行の上限（4）を超えない。
    [Fact]
    public async Task 行ごとの読み取りは並行の上限を超えない()
    {
        using var db = NewDb();
        var idp = new InMemoryServiceAccountProvisioner();
        for (var i = 0; i < 20; i++) await Provision(db, idp, $"agent-{i:00}", Attrs(("clearance", "public")));
        var counting = new CountingDirectory(idp);
        var (check, _, probe, _) = Arrange(db, counting);
        using var _p = probe;

        (await check.RunAsync(Ct)).Should().BeEmpty();

        counting.Reads.Should().Be(20);
        counting.MaxInFlight.Should().BeGreaterThan(1, "並行して読む（試験が直列で通っていないこと）")
            .And.BeLessThanOrEqualTo(IdpReconciliationOptions.MaxConcurrency);
    }

    // C-42: 行を名指しするログは 1 回に 20 件まで。超えた分は件数だけ（ログの量を有界に保つ）。ゲージは全件を数える。
    [Fact]
    public async Task 名指しのログは上限までで残りは件数だけ()
    {
        using var db = NewDb();
        for (var i = 0; i < 25; i++) await AddRow(db, $"agent-{i:00}", Attrs(("clearance", "public")));
        var (check, _, probe, log) = Arrange(db, new InMemoryServiceAccountProvisioner());
        using var _p = probe;

        (await check.RunAsync(Ct)).Should().HaveCount(25);

        probe.CollectGauge().Should().Equal(25);
        log.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("kind=client_missing")).Should().Be(20);
        log.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("ほかに 5 件"));
    }

    // C-42（PR #1831 監査 🟡1）: 名指しは重大度順（属性違い → 孤児 → 印なし → SA なし → IdP に無い）。段 1 より前の行の
    // `client_missing` が上限を超えても、セキュリティに関わる `orphan` / `attributes_differ` は名指しから押し出されない。
    // 種類ごとの件数は毎回 1 行で出す。
    [Fact]
    public async Task 名指しは重大度順で古い行の多数に押し出されず種類ごとの件数を出す()
    {
        using var db = NewDb();
        var idp = new InMemoryServiceAccountProvisioner();
        for (var i = 0; i < 21; i++) await AddRow(db, $"a-legacy-{i:00}", Attrs(("clearance", "public")));
        idp.SeedManaged("z-orphan", Attrs(("clearance", "secret")));
        await Provision(db, idp, "z-tampered", Attrs(("clearance", "public")));
        idp.Tamper("z-tampered", Attrs(("clearance", "secret")));
        var (check, _, probe, log) = Arrange(db, idp);
        using var _p = probe;

        var drifts = await check.RunAsync(Ct);

        drifts!.Select(d => d.Kind).Take(2).Should().Equal(IdpDriftKind.AttributesDiffer, IdpDriftKind.Orphan);
        var named = log.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("client=")).Select(e => e.Message).ToList();
        named.Should().HaveCount(IdpReconciliationCheck.MaxNamedDrifts);
        named[0].Should().Contain("client=z-tampered kind=attributes_differ");
        named[1].Should().Contain("client=z-orphan kind=orphan");
        log.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("ほかに 3 件"));
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information && e.Message.Contains("食い違い 23 件"))
            .Which.Message.Should().Contain("attributes_differ=1 orphan=1 enabled_differs=0 not_managed=0 service_account_missing=0 client_missing=21");
    }

    // C-43（PR #1831 監査 🟡2）: 1 行が読めないと照合全体を失敗にする（系列を止める）。Error ログはその行を名指しする。
    [Fact]
    public async Task 一行が読めなければ全体を失敗にしてその行を名指しする()
    {
        using var db = NewDb();
        var idp = new InMemoryServiceAccountProvisioner();
        await Provision(db, idp, "agent-ok", Attrs(("clearance", "public")));
        await Provision(db, idp, "agent-bad", Attrs(("clearance", "public")));
        var (check, _, probe, log) = Arrange(db, new RowFailingDirectory(idp, "agent-bad"));
        using var _p = probe;

        (await check.RunAsync(Ct)).Should().BeNull();

        probe.CollectGauge().Should().BeEmpty();
        probe.Outcome(IdpReconciliationMetrics.OutcomeFailed).Should().Be(1);
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error).Which.Message.Should().Contain("client=agent-bad");
    }

    // C-45: 周期の構成（既定 1 分・下限 1 分・`hh:mm:ss`。値域外は起動時に落とす）。
    [Theory]
    [InlineData(null, 60)]
    [InlineData("00:05:00", 300)]
    public void 周期は既定1分で構成で変えられる(string? declared, int seconds)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [IdpReconciliationOptions.IntervalKey] = declared })
            .Build();

        var options = IdpReconciliationOptions.FromConfiguration(configuration);

        options.Interval.Should().Be(TimeSpan.FromSeconds(seconds));
        options.CycleTimeout.Should().Be(options.Interval, "1 回の照合は次の周期に重ねない");
    }

    [Theory]
    [InlineData("00:00:30")]
    [InlineData("5m")]
    [InlineData("24:00:00")]
    public void 周期の値域外は起動時に落とす(string declared)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [IdpReconciliationOptions.IntervalKey] = declared })
            .Build();

        var act = () => IdpReconciliationOptions.FromConfiguration(configuration);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{IdpReconciliationOptions.IntervalKey}*");
    }

    // C-46: 常駐は起動時に 1 回、以後は周期ごとに照合する（TimeProvider で数える）。1 回の失敗で止まらない。停止で静かに終わる。
    [Fact]
    public async Task 常駐は起動時と周期ごとに照合し失敗で止まらない()
    {
        var clock = new FakeTimeProvider();
        var idp = new InMemoryServiceAccountProvisioner();
        var counting = new CountingDirectory(idp) { FailFirstList = true };
        var services = new ServiceCollection();
        var dbName = $"IdpReconciliation_{Guid.NewGuid()}";
        services.AddDbContext<McpDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddMetrics();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(Options);
        services.AddSingleton<IServiceAccountDirectory>(counting);
        services.AddSingleton<IdpReconciliationMetrics>();
        services.AddScoped<IdpReconciliationCheck>();
        services.AddSingleton<IdpReconciliationHostedService>();
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetRequiredService<IdpReconciliationHostedService>();

        await hosted.StartAsync(Ct);
        await WaitUntil(() => counting.Lists == 1);
        clock.Advance(Options.Interval);
        var metrics = provider.GetRequiredService<IdpReconciliationMetrics>();
        await WaitUntil(() => counting.Lists == 2 && metrics.LastDrifted == 0);

        metrics.LastDrifted.Should().Be(0, "1 回目の失敗の後も周期が回った");
        await hosted.StopAsync(Ct);
        hosted.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue("停止は失敗として外へ出さない");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(10, watchdog.Token);
    }

    // ---- 読み取りの偽物 --------------------------------------------------------------------------------------------

    // 指定のクライアントのサービスアカウントだけを照会で返さない（Keycloak の照会が SA を返さない場合の再現）。
    private sealed class HidingDirectory(IServiceAccountDirectory inner, string hidden) : IServiceAccountDirectory
    {
        public Task<IReadOnlyList<IdpClientEntry>> ListClientsAsync(CancellationToken ct) => inner.ListClientsAsync(ct);

        public Task<IReadOnlyDictionary<string, string>?> ReadServiceAccountAttributesAsync(string clientId, CancellationToken ct)
            => clientId == hidden
                ? Task.FromResult<IReadOnlyDictionary<string, string>?>(null)
                : inner.ReadServiceAccountAttributesAsync(clientId, ct);
    }

    private sealed class RowFailingDirectory(IServiceAccountDirectory inner, string failing) : IServiceAccountDirectory
    {
        public Task<IReadOnlyList<IdpClientEntry>> ListClientsAsync(CancellationToken ct) => inner.ListClientsAsync(ct);

        public Task<IReadOnlyDictionary<string, string>?> ReadServiceAccountAttributesAsync(string clientId, CancellationToken ct)
            => clientId == failing
                ? throw new IdpProvisioningException(IdpProvisioningFailure.Failed, "timeout")
                : inner.ReadServiceAccountAttributesAsync(clientId, ct);
    }

    private sealed class FailingDirectory(IServiceAccountDirectory inner) : IServiceAccountDirectory
    {
        public bool Fail { get; set; }

        public Task<IReadOnlyList<IdpClientEntry>> ListClientsAsync(CancellationToken ct)
            => Fail ? throw new IdpProvisioningException(IdpProvisioningFailure.Failed, "down") : inner.ListClientsAsync(ct);

        public Task<IReadOnlyDictionary<string, string>?> ReadServiceAccountAttributesAsync(string clientId, CancellationToken ct)
            => inner.ReadServiceAccountAttributesAsync(clientId, ct);
    }

    private sealed class HangingDirectory : IServiceAccountDirectory
    {
        public async Task<IReadOnlyList<IdpClientEntry>> ListClientsAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        }

        public Task<IReadOnlyDictionary<string, string>?> ReadServiceAccountAttributesAsync(string clientId, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class CountingDirectory(IServiceAccountDirectory inner) : IServiceAccountDirectory
    {
        private int _inFlight;
        private int _maxInFlight;
        private int _reads;
        private int _lists;

        public bool FailFirstList { get; init; }
        public int Reads => Volatile.Read(ref _reads);
        public int Lists => Volatile.Read(ref _lists);
        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public Task<IReadOnlyList<IdpClientEntry>> ListClientsAsync(CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _lists);
            return FailFirstList && n == 1
                ? throw new IdpProvisioningException(IdpProvisioningFailure.Failed, "down")
                : inner.ListClientsAsync(ct);
        }

        public async Task<IReadOnlyDictionary<string, string>?> ReadServiceAccountAttributesAsync(string clientId, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxInFlight)) && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen) { }
            try
            {
                await Task.Delay(20, ct);
                Interlocked.Increment(ref _reads);
                return await inner.ReadServiceAccountAttributesAsync(clientId, ct);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
