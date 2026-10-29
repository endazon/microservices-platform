using System.Collections.Concurrent;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Platform.Bff.Foundation.Session;

namespace Platform.Bff.Tests;

// NFR-19, IADR-0273 決定 5, #2141: コード交換で受けたアクセストークンから realm_access を principal へ複写する腕（OnTokenValidated）。
// 従前はトークンを解析できないとき黙ってロール無しで通し、管理画面が全員 403 になっても理由が残らなかった。
// 解析できないときは警告を 1 行残す（トークンの中身は載せない）。ロール無しで通す（fail-closed）は変えない。
public class BffSessionRealmAccessCopyTests
{
    private sealed class Capture : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => new L(this, categoryName);
        public void Dispose() { }

        private sealed class L(Capture owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => owner.Lines.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }

    private static (ServiceProvider Sp, Capture Logs) Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BffSession:Authority"] = "http://keycloak:8080/realms/platform",
                ["BffSession:ClientId"] = "bff",
                ["BffSession:ClientSecret"] = "test-secret",
                ["BffSession:RedisConnectionString"] = "localhost:6379",
            })
            .Build();
        var logs = new Capture();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs));
        services.AddBffSession(config);
        return (services.BuildServiceProvider(), logs);
    }

    private static async Task<ClaimsIdentity> RunAsync(ServiceProvider sp, string accessToken)
    {
        var options = sp.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);
        var identity = new ClaimsIdentity([new Claim("preferred_username", "alice")], "oidc");
        var ctx = new TokenValidatedContext(
            new DefaultHttpContext { RequestServices = sp },
            new AuthenticationScheme(OpenIdConnectDefaults.AuthenticationScheme, null, typeof(OpenIdConnectHandler)),
            options, new ClaimsPrincipal(identity), new AuthenticationProperties())
        {
            TokenEndpointResponse = new OpenIdConnectMessage { AccessToken = accessToken },
        };
        await options.Events.OnTokenValidated(ctx);
        return identity;
    }

    [Theory]
    [InlineData("not-a-jwt-SECRETVALUE")]
    [InlineData("a.b")]
    public async Task 解析できないアクセストークンはロール無しで通し警告を1行残す(string accessToken)
    {
        var (sp, logs) = Build();
        using var _ = sp;

        var identity = await RunAsync(sp, accessToken);

        identity.HasClaim(c => c.Type == "realm_access").Should().BeFalse("fail-closed は変えない");
        var warnings = logs.Lines.Where(l => l.Level == LogLevel.Warning).ToList();
        warnings.Should().ContainSingle();
        warnings[0].Message.Should().Contain("realm_access").And.NotContain("SECRETVALUE");
    }

    [Fact]
    public async Task 解析できるトークンのrealm_accessは複写し警告を出さない()
    {
        var (sp, logs) = Build();
        using var _ = sp;
        var token = new JsonWebTokenHandler().CreateToken(
            """{"sub":"alice","realm_access":{"roles":["platform-admin"]}}""");

        var identity = await RunAsync(sp, token);

        identity.FindFirst("realm_access")!.Value.Should().Contain("platform-admin");
        logs.Lines.Where(l => l.Level == LogLevel.Warning).Should().BeEmpty();
    }
}
