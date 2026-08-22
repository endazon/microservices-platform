using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Wolverine;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Extensions;

// ADR-0027 / NFR: Wolverine へ移した発行元のブローカ readiness（W4・段 A）。
//
// 🔴 **本試験群が守るのは「移行した瞬間に readiness が黙って消える」形である。**
// MassTransit は AddMassTransit が "masstransit-bus"（tag ready）を暗黙に登録していた。
// Wolverine は AddWolverine ＋ UseRabbitMq で **ヘルスチェックを 0 件しか登録しない**（実測）。
// 気づかずに移すと /health/ready はブローカ不達でも 200 を返す。
//
// ⚠️ **実 HTTP で 503 を測る段 B は本ファイルに無い。** それには
// Microsoft.AspNetCore.Mvc.Testing と HealthCheckExtensionsTests の器が要り、
// どちらも並行 PR #931 が持っている。段 B が入るまで W4 は完了しない。
public class WolverineBrokerHealthCheckTests
{
    private static IReadOnlyList<HealthCheckRegistration> RegistrationsOf(IServiceCollection services)
    {
        // AddHealthChecks は IOptions 基盤を登録しない（チェック 0 件だと Configure が 1 度も走らない）。
        // 観測のために試験側で足す。本番は他の登録が必ず AddOptions を通す。
        using var provider = services.AddOptions().BuildServiceProvider();
        return [.. provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations];
    }

    [Fact]
    public void 適用前の既定_共通ヘルパ単独ではチェックを1件も登録しない()
    {
        // 🔴 opt-in であることの前提条件。ここが 0 件でなければ、
        // 「自動登録していない」という本 PR の主張そのものが崩れる。
        // 並行 PR #931 の基準テスト（チェック 0 件なら両方 200）とも同じ前提を共有する。
        var services = new ServiceCollection();

        services.AddPlatformHealthChecks();

        RegistrationsOf(services).Should().BeEmpty();
    }

    [Fact]
    public void ブローカチェックはopt_inで登録され_readyタグを持つ()
    {
        // `ready` タグは MapPlatformHealthChecks の /health/ready が拾う唯一の条件である。
        // これが無いと登録はされるのに readiness には一生載らない（登録数だけ見ると緑）。
        var services = new ServiceCollection();

        services.AddPlatformHealthChecks().AddPlatformWolverineBroker();

        var registration = RegistrationsOf(services).Should().ContainSingle().Subject;
        registration.Name.Should().Be(WolverineExtensions.BrokerHealthCheckName);
        registration.Tags.Should().Contain("ready");
        registration.FailureStatus.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public void 登録名は呼び出し側で変えられる()
    {
        var services = new ServiceCollection();

        services.AddPlatformHealthChecks().AddPlatformWolverineBroker("broker-x");

        RegistrationsOf(services).Should().ContainSingle().Subject.Name.Should().Be("broker-x");
    }

    [Fact]
    public async Task 外部トランスポートが1つも無ければUnhealthyを返す()
    {
        // 🔴 「繋ぎ先が無いので健全」と読まない。配線忘れが緑になるのを防ぐ。
        // ブローカを立てずに測れる唯一の挙動分岐であり、段 B（実 HTTP の 503）とは別物である。
        //
        // 実装型は internal なので直接 new せず、**本番と同じ経路**（HealthCheckService）で引く。
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.DisableConventionalDiscovery())
            .ConfigureServices(s => s.AddPlatformHealthChecks().AddPlatformWolverineBroker())
            .Build();

        var report = await host.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(TestContext.Current.CancellationToken);

        var entry = report.Entries.Should().ContainKey(WolverineExtensions.BrokerHealthCheckName)
            .WhoseValue;
        entry.Status.Should().Be(HealthStatus.Unhealthy);
        entry.Description.Should().Contain("ブローカのトランスポート");
    }
}
