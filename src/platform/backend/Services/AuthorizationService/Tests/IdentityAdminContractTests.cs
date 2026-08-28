using AuthorizationService.Domain.Ports;
using AuthorizationService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        var act = () => services.AddIdentityAdminClient(empty);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*IdentityAdmin:Provider*");
    }

    [Fact]
    public void Registration_fails_on_an_unknown_provider()
    {
        var services = new ServiceCollection();
        var config = Config(new() { ["IdentityAdmin:Provider"] = "ldap" });

        var act = () => services.AddIdentityAdminClient(config);

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

        var act = () => new ServiceCollection().AddIdentityAdminClient(Config(settings));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{missing}*");
    }

    // 陽性対照: 資格情報が揃えば keycloak 実装が解決できる。
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
        }));

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
        services.AddIdentityAdminClient(Config(new() { ["IdentityAdmin:Provider"] = "in-memory" }));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IIdentityAdminClient>()
            .Should().BeOfType<InMemoryIdentityAdminClient>();

        recorder.Warnings.Should().ContainSingle().Which.Should().Contain("反映されない");
    }

    private static IConfiguration Config(Dictionary<string, string?> settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

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
