using AwesomeAssertions;
using McpServer.Domain.Ports;
using McpServer.Infrastructure.ExternalServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace McpServer.Tests.Infrastructure.ExternalServices;

// FR-16, SC-12, 計画 ADR-0123 決定 4, [[IADR-0515]] 決定 2・5 (#1786): IdP への書き込み口の選択。
[Trait("TestKind", "Unit")]
public class ServiceAccountProvisioningRegistrationTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "McpServer";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IServiceCollection Register(string environment, params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new Env(environment));
        services.AddServiceAccountProvisioning();
        return services;
    }

    private static IServiceAccountProvisioner Resolve(IServiceCollection services)
        => services.BuildServiceProvider().GetRequiredService<IServiceAccountProvisioner>();

    // T-1786-41: 選択は解決時に行い（Program.cs が起動時に 1 度解決して落とす）、未設定は起動を止めず、書き込み口の無い実装（無人を 503 で拒む）になる。
    [Fact]
    public void 未設定は書き込み口の無い実装になる()
        => Resolve(Register("Production")).Should().BeOfType<UnconfiguredServiceAccountProvisioner>();

    // T-1786-42（否定形）: 偽の口は配備ホストで選べない（IADR-0329 と同じ許可集合）。
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Prod")]
    public void 偽の口は配備ホストで選べない(string environment)
    {
        var act = () => Resolve(Register(environment, ("McpClientProvisioning:Provider", "in-memory")));
        act.Should().Throw<InvalidOperationException>().WithMessage("*非配備ホスト*");
    }

    // T-1786-43: keycloak は資格情報が揃わなければ起動時に落ちる（既定の資格情報を持たない）。
    [Fact]
    public void keycloakは資格情報が無ければ落ちる()
    {
        var act = () => Resolve(Register("Production",
            ("McpClientProvisioning:Provider", "keycloak"),
            ("McpClientProvisioning:Keycloak:BaseUrl", "http://keycloak:8080"),
            ("McpClientProvisioning:Keycloak:Realm", "platform"),
            ("McpClientProvisioning:Keycloak:ClientId", "mcp-client-admin")));
        act.Should().Throw<InvalidOperationException>().WithMessage("*ClientSecret*");
    }

    // T-1786-44（陽性対照）: 揃えば Keycloak の口になる。
    [Fact]
    public void keycloakは資格情報が揃えばKeycloakの口になる()
        => Resolve(Register("Production",
                ("McpClientProvisioning:Provider", "keycloak"),
                ("McpClientProvisioning:Keycloak:BaseUrl", "http://keycloak:8080"),
                ("McpClientProvisioning:Keycloak:Realm", "platform"),
                ("McpClientProvisioning:Keycloak:ClientId", "mcp-client-admin"),
                ("McpClientProvisioning:Keycloak:ClientSecret", "injected-at-deploy-time")))
            .Should().BeOfType<KeycloakServiceAccountProvisioner>();

    // T-1786-45: 値域外は起動時に落とす。
    [Fact]
    public void 値域外の口は落ちる()
    {
        var act = () => Resolve(Register("Development", ("McpClientProvisioning:Provider", "ldap")));
        act.Should().Throw<InvalidOperationException>().WithMessage("*不正*");
    }
}
