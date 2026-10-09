using AwesomeAssertions;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Bff.Foundation.Secrets;
using Platform.Bff.Foundation.Session;
using StackExchange.Redis;
using System.Security.Claims;

namespace Platform.Bff.Tests;

// NFR-18, ADR-0131 決定 4 の受入条件 1・2, ADR-0032, IADR-0251 決定 4・5, [[IADR-0510]], [[IADR-0522]] (#1839):
// **BFF の 3 用途 —— セッションの保存と失効（全セッションの即時失効を含む）・DataProtection の鍵リングの共有・
// SC-22 の書き込み記録 —— が、digest で固定した Valkey の実イメージで通ること。**
//
// 🔴 既存の試験（RedisTicketStoreTests・BffKeyRingSharingTests・SC-22 の記録の試験）は記憶域を偽物
// （MemoryDistributedCache・プロセス内のリスト）へ差し替えており、**実イメージには届かない**。本クラスは同じ操作を、
// 本番の配線（`AddBffSession`。接続先とパスワードを `BffSession` の構成から組む）のまま実イメージへ向けて回す。
// クライアントは StackExchange.Redis のまま（ADR-0131 決定 2）。ストアは配備と同じ起動形（認証必須）で起こす
// （ValkeyTestContainer。一致は ValkeyContainerDefinitionTests が PR で突き合わせる）。
//
// 🔴 Trait が無いと integration.yml の一覧（Category=Integration）に拾われない。PR の ci.yml は本クラスを外す。
[Trait("Category", "Integration")]
public sealed class BffSessionStoreValkeyTests
{
    // 鍵リングのキー（BffKeyRingSharingTests と同じ理由で定数を引かずにリテラルで固定する）。
    private const string KeyRingKey = "bff:dataprotection-keys";

    // 試験ごとに乱数のパスワード（実在の鍵に見えない形。gitleaks）。
    private static string NewPassword() => "dummy-" + Guid.NewGuid().ToString("N");

    private static async Task<IContainer> StartAsync(string password)
    {
        // 依存（Docker）を得られなければ理由つきで skip する。文言の先頭は回収実行の門
        // （check-integration-executed.js の DEPENDENCY_SKIP_MARKERS）が「依存を得られない skip」と数える目印である。
        Assert.SkipUnless(
            DockerAvailable(),
            "この試験が要るサービスを得られない: Valkey（Testcontainers）。コンテナランタイム（Docker Engine API）を起動すること");
        var container = ValkeyTestContainer.Build(password);
        await container.StartAsync(TestContext.Current.CancellationToken);
        return container;
    }

    /// <summary>本番の配線（AddBffSession）で BFF のレプリカ 1 つ分のサービスを組む。</summary>
    private static ServiceProvider Replica(IContainer store, string password)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BffSession:RedisConnectionString"] = ValkeyTestContainer.EndpointOf(store),
                ["BffSession:RedisPassword"] = password,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBffSession(config);
        return services.BuildServiceProvider();
    }

    private static async Task<ConnectionMultiplexer> ConnectAsync(IContainer store, string? password) =>
        await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { ValkeyTestContainer.EndpointOf(store) },
            Password = password,
            AbortOnConnectFail = false,
            ConnectTimeout = 5000,
        });

    private static AuthenticationTicket TicketFor(string subject) =>
        new(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], BffSessionExtensions.SessionScheme)),
            new AuthenticationProperties(), BffSessionExtensions.SessionScheme);

    // 受入条件 2（基準 D）: ストアは認証なしの接続を受け付けない（陰性対照。以下の陽性が「認証なしでも通る」で緑にならない）。
    [Fact]
    public async Task Store_refuses_unauthenticated_clients()
    {
        await using var store = await StartAsync(NewPassword());
        using var anonymous = await ConnectAsync(store, password: null);

        var act = () => anonymous.GetDatabase().StringSetAsync("probe", "x");

        await act.Should().ThrowAsync<RedisException>();
    }

    // 受入条件 1 ①: セッションの保存・取得・更新・単独の失効・全セッションの即時失効（ADR-0032）。
    [Fact]
    public async Task Sessions_round_trip_and_all_sessions_of_a_subject_are_revoked_at_once()
    {
        var password = NewPassword();
        await using var store = await StartAsync(password);
        await using var replica = Replica(store, password);
        var tickets = replica.GetRequiredService<RedisTicketStore>();

        var alice1 = await tickets.StoreAsync(TicketFor("alice"));
        var alice2 = await tickets.StoreAsync(TicketFor("alice"));
        var bob = await tickets.StoreAsync(TicketFor("bob"));
        (await tickets.RetrieveAsync(alice1))!.Principal.FindFirst("sub")!.Value.Should().Be("alice");

        await tickets.RenewAsync(alice1, TicketFor("alice"));
        (await tickets.RetrieveAsync(alice1)).Should().NotBeNull();

        await tickets.RemoveAsync(alice2);
        (await tickets.RetrieveAsync(alice2)).Should().BeNull("単独の失効は即時");

        var alice3 = await tickets.StoreAsync(TicketFor("alice"));
        (await tickets.RemoveAllForSubjectAsync("alice")).Should().Be(2);
        (await tickets.RetrieveAsync(alice1)).Should().BeNull("全セッションの即時失効");
        (await tickets.RetrieveAsync(alice3)).Should().BeNull("全セッションの即時失効");
        (await tickets.RetrieveAsync(bob)).Should().NotBeNull("他の利用者のセッションは残る");
    }

    // 受入条件 1 ②: 2 つのレプリカが同じ鍵リングを引き、互いの保護した値を復号できる（IADR-0251 決定 5・IADR-0510）。
    [Fact]
    public async Task Replicas_share_the_data_protection_key_ring()
    {
        var password = NewPassword();
        await using var store = await StartAsync(password);
        await using var a = Replica(store, password);
        var protectedByA = a.GetRequiredService<IDataProtectionProvider>().CreateProtector("cookie").Protect("payload");

        await using var b = Replica(store, password);
        b.GetRequiredService<IDataProtectionProvider>().CreateProtector("cookie").Unprotect(protectedByA).Should().Be("payload");

        using var probe = await ConnectAsync(store, password);
        (await probe.GetDatabase().ListLengthAsync(KeyRingKey)).Should().Be(1, "鍵はストアの共有のキーにちょうど 1 件");
    }

    // 受入条件 1 ③: SC-22 の書き込み記録（最終更新者）が別のレプリカから読める（IADR-0453 決定 3）。
    [Fact]
    public async Task Secret_write_records_are_shared_across_replicas()
    {
        var password = NewPassword();
        await using var store = await StartAsync(password);
        await using var a = Replica(store, password);
        await using var b = Replica(store, password);
        var record = new SecretWriteRecord(3, "operator-1", "apiKey", new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero));
        var ct = TestContext.Current.CancellationToken;

        await new DistributedCacheSecretWriteRecordStore(
            a.GetRequiredService<IDistributedCache>(), NullLogger<DistributedCacheSecretWriteRecordStore>.Instance)
            .SaveAsync("wikijs-sync", record, ct);
        var read = await new DistributedCacheSecretWriteRecordStore(
            b.GetRequiredService<IDistributedCache>(), NullLogger<DistributedCacheSecretWriteRecordStore>.Instance)
            .GetAsync("wikijs-sync", ct);

        // 🔴 記録の置き場は失敗を握って「記録なし」へ倒すので、null は認証の失敗も含む。値の一致で確かめる。
        read.Should().Be(record);
        using var probe = await ConnectAsync(store, password);
        (await probe.GetDatabase().KeyExistsAsync("bff:sc22:write-record:wikijs-sync")).Should().BeTrue();
    }

    // ヘルスチェック（Program.cs の AddRedis）も同じ構成（接続先 ＋ パスワード）で通る。
    [Fact]
    public async Task Health_check_authenticates_with_the_session_configuration()
    {
        var password = NewPassword();
        await using var store = await StartAsync(password);
        var options = new BffSessionOptions { RedisConnectionString = ValkeyTestContainer.EndpointOf(store), RedisPassword = password };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks().AddRedis(_ => ConnectionMultiplexer.Connect(options.SessionStoreConfiguration()));
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);

        report.Status.Should().Be(HealthStatus.Healthy, string.Join(" / ", report.Entries.Select(e => $"{e.Key}: {e.Value.Description}")));
    }

    /// <summary>Docker Engine API へ届くか（DOCKER_HOST か既定のソケット）。</summary>
    private static bool DockerAvailable() =>
        Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 }
        || (OperatingSystem.IsWindows() ? File.Exists(@"\\.\pipe\docker_engine") : File.Exists("/var/run/docker.sock"));
}
