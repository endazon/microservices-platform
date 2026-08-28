using AwesomeAssertions;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Extensions;

// ADR-0027 / ADR-0028, NFR (#901): WolverineBrokerHealthCheck の**集約判定**を固定する。
//
// 🔴 **本ファイルが塞ぐ穴。** 既存の WolverineBrokerHealthCheckTests は登録面（3 件）と
// 「ブローカのトランスポートが 1 件も無い → Unhealthy」（1 件）しか通しておらず、
// **ブローカが実際に不健全なときの判定は 1 本も試験されていなかった**
// （着手前の実測: WolverineExtensions.cs は branch 1/18 = 5.6%。167-244 行が未実行）。
//
// この経路が壊れると **/health/ready はブローカ不達でも 200 を返す**。
// 起動もビルドもテストも通り、k8s は publish できない pod へトラフィックを流し続ける。
// 実装自身のコメントが「無いのと同じであるうえに**在るように見える**ぶん悪い」と書いている形である。
//
// 実装型は internal だが（InternalsVisibleTo 済み）、コンストラクタ引数 IWolverineRuntime は
// 約 30 メンバのインタフェースでダブルを書くのは非現実的である。かわりに**実 Wolverine ホストを建て、
// WolverineOptions.Transports.Add（public API）で偽トランスポートを差し込む**。
// 実装は transport.GetType().GetMethod(..., DeclaredOnly) で BuildHealthCheck を解決するため、
// 偽トランスポート側に public な BuildHealthCheck を宣言すれば本番と同じ経路に乗る。
public class WolverineBrokerHealthAggregationTests
{
    // 本番と同じ経路（HealthCheckService）で 1 回引く。
    private static async Task<HealthReportEntry> CheckAsync(params ITransport[] transports)
    {
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                foreach (var t in transports) opts.Transports.Add(t);
            })
            .ConfigureServices(s => s.AddPlatformHealthChecks().AddPlatformWolverineBroker())
            .Build();

        var report = await host.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(TestContext.Current.CancellationToken);

        return report.Entries[WolverineExtensions.BrokerHealthCheckName];
    }

    private static TransportHealthResult Result(
        string protocol, TransportHealthStatus status, string message) =>
        new(protocol + "-transport", protocol, status, message, DateTimeOffset.UnixEpoch, []);

    // ── ブローカが不健全: 200 を返さない ────────────────────────────────────────

    [Fact]
    public async Task ブローカがUnhealthyならreadinessもUnhealthyで理由を運ぶ()
    {
        var entry = await CheckAsync(FakeTransport.Returning(
            "rabbitmq", Result("rabbitmq", TransportHealthStatus.Unhealthy, "接続拒否")));

        entry.Status.Should().Be(HealthStatus.Unhealthy);
        // protocol と理由の両方が載らないと、運用は「どのブローカがなぜ落ちたか」を読めない。
        entry.Description.Should().Contain("rabbitmq").And.Contain("接続拒否");
    }

    // 🔴 実装コメントの「例外を握り潰して Healthy を返さない」そのもの。
    [Fact]
    public async Task ヘルスチェックの取得が例外でもHealthyへ倒さない()
    {
        var entry = await CheckAsync(FakeTransport.Throwing(
            "rabbitmq", new InvalidOperationException("broker unreachable")));

        entry.Status.Should().Be(HealthStatus.Unhealthy);
        entry.Description.Should().Contain("rabbitmq").And.Contain("broker unreachable");
    }

    // 🔴 「観測できない」を「異常が無い」と読まない。Wolverine の版更新で BuildHealthCheck の
    // 形が変わると null が返る（実装コメントが実測として記録している事故）。
    [Fact]
    public async Task ヘルスチェックを取得できなければUnhealthyにする()
    {
        var entry = await CheckAsync(FakeTransport.Returning("rabbitmq", healthCheck: null));

        entry.Status.Should().Be(HealthStatus.Unhealthy);
        entry.Description.Should().Contain("取得できませんでした");
    }

    // ── 3 値の縮退を潰さない ───────────────────────────────────────────────────

    [Fact]
    public async Task Degradedのみなら全体もDegradedにする()
    {
        var entry = await CheckAsync(FakeTransport.Returning(
            "rabbitmq", Result("rabbitmq", TransportHealthStatus.Degraded, "キュー滞留")));

        // Unhealthy へ丸めると過剰に pod を落とし、Healthy へ丸めると劣化を見逃す。
        entry.Status.Should().Be(HealthStatus.Degraded);
        entry.Description.Should().Contain("キュー滞留");
    }

    // 対照条件。これが無いと「常に Unhealthy を返す」実装でも上の 4 件は通る。
    [Fact]
    public async Task 全てHealthyならHealthyを返す()
    {
        var entry = await CheckAsync(FakeTransport.Returning(
            "rabbitmq", Result("rabbitmq", TransportHealthStatus.Healthy, "ok")));

        entry.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task UnhealthyとDegradedが混在すればUnhealthyが勝つ()
    {
        var entry = await CheckAsync(
            FakeTransport.Returning("rabbitmq", Result("rabbitmq", TransportHealthStatus.Degraded, "遅延")),
            FakeTransport.Returning("kafka", Result("kafka", TransportHealthStatus.Unhealthy, "断線")));

        // 悪い方へ倒す。Degraded で返すと「publish できない」pod が ready のまま残る。
        entry.Status.Should().Be(HealthStatus.Unhealthy);
        entry.Description.Should().Contain("断線");
        entry.Description.Should().NotContain("遅延", "Unhealthy が勝つときは Degraded を混ぜない");
    }

    [Fact]
    public async Task 複数ブローカのUnhealthyは全て理由に併記する()
    {
        var entry = await CheckAsync(
            FakeTransport.Returning("rabbitmq", Result("rabbitmq", TransportHealthStatus.Unhealthy, "断線A")),
            FakeTransport.Returning("kafka", Result("kafka", TransportHealthStatus.Unhealthy, "断線B")));

        entry.Status.Should().Be(HealthStatus.Unhealthy);
        // 1 件目で打ち切ると、2 つ目のブローカの障害が運用に届かない。
        entry.Description.Should().Contain("断線A").And.Contain("断線B");
    }

    // ── allowlist（denylist へ退行させない） ──────────────────────────────────

    // 🔴 素の Wolverine ホストは常に stub / local / tcp を持つ（実装コメントの実測）。
    // denylist にすると版が増えるたびに新しい組み込みトランスポートへ検査を掛けて落ちる。
    // ここは「ブローカが 0 件」の Unhealthy になるのが正しい。
    [Fact]
    public async Task 組み込みトランスポートはブローカとして数えない()
    {
        var entry = await CheckAsync(FakeTransport.Throwing(
            "stub-like", new InvalidOperationException("検査対象外なので呼ばれてはならない")));

        entry.Status.Should().Be(HealthStatus.Unhealthy);
        entry.Description.Should().Contain("ブローカのトランスポート");
        entry.Description.Should().NotContain("呼ばれてはならない",
            "allowlist 外の protocol にヘルスチェックを掛けてはならない");
    }

    // 停止要求を「ブローカ異常」に化けさせない（catch の when 条件）。
    [Fact]
    public async Task 停止要求は握らずに伝播する()
    {
        var transport = FakeTransport.Throwing(
            "rabbitmq", new OperationCanceledException("停止要求"));

        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Transports.Add(transport);
            })
            .ConfigureServices(s => s.AddPlatformHealthChecks().AddPlatformWolverineBroker())
            .Build();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var check = CreateCheck(runtime);

        // HealthCheckService は例外を Unhealthy へ包んでしまうため、実装型を直接呼んで観測する。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken));
    }

    // internal 型を名前で起こす（型名を直接書くと internal 参照になり、可視性の変更に引きずられる）。
    private static IHealthCheck CreateCheck(IWolverineRuntime runtime)
    {
        var type = typeof(WolverineExtensions).Assembly
            .GetType("Platform.Shared.Infrastructure.Foundation.Extensions.WolverineBrokerHealthCheck")!;
        return (IHealthCheck)Activator.CreateInstance(type, runtime)!;
    }

    // ── テスト用のトランスポート・ダブル ──────────────────────────────────────

    private sealed class FakeHealthCheck(
        string protocol, TransportHealthResult? result, Exception? failure)
        : WolverineTransportHealthCheck
    {
        public override string TransportName => protocol + "-transport";
        public override string Protocol => protocol;

        public override Task<TransportHealthResult> CheckHealthAsync(
            CancellationToken cancellationToken = default) =>
            failure is not null ? Task.FromException<TransportHealthResult>(failure)
                                : Task.FromResult(result!);
    }

    // ITransport の実装。**BuildHealthCheck を public に宣言する**ことが要点で、
    // 実装側の DeclaredOnly なリフレクション解決がここに当たる。
    private sealed class FakeTransport : ITransport
    {
        private readonly TransportHealthResult? _result;
        private readonly Exception? _failure;
        private readonly bool _nullCheck;

        private FakeTransport(string protocol, TransportHealthResult? result, Exception? failure, bool nullCheck)
        {
            Protocol = protocol;
            _result = result;
            _failure = failure;
            _nullCheck = nullCheck;
        }

        public static FakeTransport Returning(string protocol, TransportHealthResult? healthCheck) =>
            new(protocol, healthCheck, null, nullCheck: healthCheck is null);

        public static FakeTransport Throwing(string protocol, Exception failure) =>
            new(protocol, null, failure, nullCheck: false);

        public string Protocol { get; }
        public string Name => Protocol + "-transport";

        public WolverineTransportHealthCheck BuildHealthCheck(IWolverineRuntime runtime) =>
            _nullCheck ? null! : new FakeHealthCheck(Protocol, _result, _failure);

        public string Describe() => Name;
        public string DescribeEndpoint() => Name;
        public Endpoint ReplyEndpoint() => throw new NotSupportedException("本試験では使わない");
        public Endpoint GetOrCreateEndpoint(Uri uri) => throw new NotSupportedException("本試験では使わない");
        public Endpoint? TryGetEndpoint(Uri uri) => null;
        public IEnumerable<Endpoint> Endpoints() => [];
        public ValueTask InitializeAsync(IWolverineRuntime runtime) => ValueTask.CompletedTask;
        public ValueTask InitializeEndpointsAsync(IWolverineRuntime runtime) => ValueTask.CompletedTask;
        public bool TryBuildStatefulResource(IWolverineRuntime runtime, out IStatefulResource? resource)
        {
            resource = null;
            return false;
        }

        public bool TryBuildBrokerUsage(out BrokerDescription description)
        {
            // インタフェース側は非 null 宣言だが false のときは読まれない契約である。
            description = null!;
            return false;
        }
    }
}
