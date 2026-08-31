using AuthorizationService.Domain.Ports;
using AuthorizationService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AuthorizationService.Tests;

// FR-05, FR-09, SC-17, ADR-0026, IADR-0301 (#452): 身元管理の抽象そのものに掛ける固定。
public class IdentityAdminContractTests
{
    // 🔴 計画 05_screens §SC-17 アクション:「**本画面から新規作成はしない**」。
    // 規約ではなく**型で持てなくする**。ここが赤くなるのは、誰かが作成の口を生やしたときである。
    [Fact]
    public void The_port_exposes_no_way_to_create_a_user()
    {
        var forbidden = new[] { "Create", "Register", "Add", "Provision", "Invite", "Import" };

        var offending = typeof(IIdentityAdminClient).GetMethods()
            .Select(m => m.Name)
            .Where(name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        offending.Should().BeEmpty(
            "計画 05_screens §SC-17 はアカウントを人事システム連携で自動プロビジョニングすると定め、"
            + "本画面からの新規作成を禁じている");
    }

    // 陽性対照。上の否定形だけだと、**メソッドが 1 つも無い空のインターフェイス**でも緑になる。
    [Fact]
    public void The_port_exposes_the_five_operations_the_screen_needs()
        => typeof(IIdentityAdminClient).GetMethods().Select(m => m.Name)
            .Should().BeEquivalentTo(
                nameof(IIdentityAdminClient.ListUsersAsync),
                nameof(IIdentityAdminClient.ListAssignableRolesAsync),
                nameof(IIdentityAdminClient.ReplaceAttributesAsync),
                nameof(IIdentityAdminClient.ReplaceRealmRolesAsync),
                nameof(IIdentityAdminClient.SetEnabledAsync),
                nameof(IIdentityAdminClient.RevokeSessionsAsync));

    // IADR-0301 決定 3: **provider の宣言に既定は無い。** 未宣言は起動時に落ちる。
    [Fact]
    public void Registration_fails_when_the_provider_is_not_declared()
    {
        var services = new ServiceCollection();
        var empty = new ConfigurationBuilder().Build();

        var act = () => services.AddIdentityAdminClient(empty, Env(Environments.Development));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*IdentityAdmin:Provider*");
    }

    [Fact]
    public void Registration_fails_on_an_unknown_provider()
    {
        var services = new ServiceCollection();
        var config = Config(new() { ["IdentityAdmin:Provider"] = "ldap" });

        var act = () => services.AddIdentityAdminClient(config, Env(Environments.Development));

        act.Should().Throw<InvalidOperationException>().WithMessage("*ldap*");
    }

    // NFR, #1012 / IADR-0286 と同型: **既定資格情報を持たない。** keycloak を選んだのに
    // 資格情報が無ければ、接続時ではなく**起動時**に落ちる。
    [Theory]
    [InlineData("BaseUrl")]
    [InlineData("Realm")]
    [InlineData("ClientId")]
    [InlineData("ClientSecret")]
    public void Registration_fails_when_a_keycloak_credential_is_missing(string missing)
    {
        var settings = new Dictionary<string, string?>
        {
            ["IdentityAdmin:Provider"] = "keycloak",
            ["IdentityAdmin:Keycloak:BaseUrl"] = "https://auth.example.test",
            ["IdentityAdmin:Keycloak:Realm"] = "platform",
            ["IdentityAdmin:Keycloak:ClientId"] = "user-admin",
            ["IdentityAdmin:Keycloak:ClientSecret"] = "injected-at-deploy-time",
        };
        settings.Remove($"IdentityAdmin:Keycloak:{missing}");

        var act = () => new ServiceCollection()
            .AddIdentityAdminClient(Config(settings), Env(Environments.Production));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{missing}*");
    }

    // 陽性対照: 資格情報が揃えば keycloak 実装が解決できる。**本番相当の環境で**解決することが要点で、
    // 実配備（Production）はこの経路を通る（IADR-0321 (#1101)）。
    [Fact]
    public void Registration_resolves_the_keycloak_client_when_every_credential_is_injected()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddIdentityAdminClient(Config(new()
        {
            ["IdentityAdmin:Provider"] = "keycloak",
            ["IdentityAdmin:Keycloak:BaseUrl"] = "https://auth.example.test",
            ["IdentityAdmin:Keycloak:Realm"] = "platform",
            ["IdentityAdmin:Keycloak:ClientId"] = "user-admin",
            ["IdentityAdmin:Keycloak:ClientSecret"] = "injected-at-deploy-time",
        }), Env(Environments.Production));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IIdentityAdminClient>()
            .Should().BeOfType<KeycloakIdentityAdminClient>();
    }

    // in-memory を選ぶと**警告が出る**。黙って偽物が動く形にしない。
    [Fact]
    public void The_in_memory_provider_warns_that_it_does_not_reach_the_real_idp()
    {
        var recorder = new RecordingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(recorder).SetMinimumLevel(LogLevel.Debug));
        services.AddIdentityAdminClient(
            Config(new() { ["IdentityAdmin:Provider"] = "in-memory" }), Env(Environments.Development));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IIdentityAdminClient>()
            .Should().BeOfType<InMemoryIdentityAdminClient>();

        recorder.Warnings.Should().ContainSingle().Which.Should().Contain("反映されない");
    }

    // FR-05, FR-09, SC-17, IADR-0321 (#1101): **偽の身元プロバイダは非配備ホストでしか選べない。**
    //
    // 🔴 稼働 dev クラスタは `IdentityAdmin__Provider=in-memory` のまま動いており、SC-17 の
    // 無効化・ロール変更・属性変更は Keycloak へ 1 件も届いていなかった（画面は 200 を返し、
    // 次の Pod 再起動まで変更が残って見える）。宣言を必須にしただけでは、**配備が明示的に偽物を
    // 宣言すること**を止められない。警告ログ 1 行は運用が見落とす。
    //
    // 🔴 **`Production` を先頭に置くのは、環境変数を与えない配備がその名前になるからである** ——
    // #1101 で壊れていた稼働 Pod は `ASPNETCORE_ENVIRONMENT` を持たず、まさにこの経路を通る。
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Prod")]        // 名前を勝手に付けた配備も通さない（許可集合＝deny by default）
    public void The_in_memory_provider_cannot_be_selected_on_a_deployed_host(string environmentName)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var act = () => services.AddIdentityAdminClient(
            Config(new() { ["IdentityAdmin:Provider"] = "in-memory" }), Env(environmentName));

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(environmentName,
                "落ちた理由（どの環境で偽物を宣言したのか）が分からないと、配備側は直しようがない");
    }

    // 陽性対照。上の否定形だけでは「**常に**落ちる」実装と区別がつかない。
    // **本リポジトリが実際に使う 3 つの非配備ホスト名すべて**で通ることを固定する
    // （`Testing` は各サービスの TestWebApplicationFactory、`Integration` は
    // Knowledge.IntegrationTests の器が宣言する名前である。どちらかを取りこぼすと、
    // 既存のテスト器が丸ごと起動しなくなる）。
    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("Integration")]
    public void The_in_memory_provider_is_selectable_on_every_non_deployed_host(string environmentName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIdentityAdminClient(
            Config(new() { ["IdentityAdmin:Provider"] = "in-memory" }), Env(environmentName));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IIdentityAdminClient>()
            .Should().BeOfType<InMemoryIdentityAdminClient>();
    }

    // 許可集合そのものを固定する。**器が宣言する環境名と 1 対 1 で対応している**ことが要点で、
    // ここへ配備の環境名（Production 等）が紛れ込んだら赤くする。
    [Fact]
    public void The_allow_list_names_only_non_deployed_hosts()
        => IdentityAdminRegistration.NonDeployedEnvironments
            .Should().BeEquivalentTo("Development", "Testing", "Integration");

    private static IConfiguration Config(Dictionary<string, string?> settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    // 実行環境の宣言だけを持つ最小の実装（ファイルプロバイダ等は本検査に無関係）。
    private static IHostEnvironment Env(string environmentName) => new TestEnvironment
    {
        EnvironmentName = environmentName,
        ApplicationName = "AuthorizationService.Tests",
        ContentRootPath = AppContext.BaseDirectory,
        ContentRootFileProvider = new NullFileProvider(),
    };

    private sealed class TestEnvironment : IHostEnvironment
    {
        public required string EnvironmentName { get; set; }
        public required string ApplicationName { get; set; }
        public required string ContentRootPath { get; set; }
        public required IFileProvider ContentRootFileProvider { get; set; }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<string> Warnings { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Recorder(Warnings);
        public void Dispose() { }

        private sealed class Recorder(List<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning) sink.Add(formatter(state, exception));
            }
        }
    }
}
