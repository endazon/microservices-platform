using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Extensions;

// NFR-18, ADR-0084 決定 1, IADR-0686 (#2430・AI レビュー): 段階つきの認証の門の単体試験。
// 🔴 段 2 へ上げる前提は「段 1 の計器 auth.internal_gate.anonymous.total が 0」なので、計器の配線（群・段の属性）を固定する。
[Trait("TestKind", "Unit")]
public class InternalAuthGateTests
{
    private const string UserHeader = "X-Test-User";

    private sealed class HeaderAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var user))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, user.ToString())], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    private static async Task<WebApplication> StartAsync(string? mode)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        if (mode is not null)
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [InternalAuthGate.ModeConfigKey] = mode });
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddInternalAuthGate(builder.Configuration);

        var app = builder.Build();
        app.UsePlatformMiddleware();
        app.MapGroup("/g").RequireAuthenticatedOrReport("unit-group").MapGet("/x", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private sealed class Probe : IDisposable
    {
        private readonly MeterListener _l = new();
        private readonly List<(long Value, Dictionary<string, string> Tags)> _seen = [];

        public Probe()
        {
            _l.InstrumentPublished = (i, l) =>
            {
                if (i.Meter.Name == InternalAuthGate.MeterName && i.Name == InternalAuthGate.AnonymousCounterName)
                    l.EnableMeasurementEvents(i);
            };
            _l.SetMeasurementEventCallback<long>((_, v, tags, _) =>
            {
                var d = new Dictionary<string, string>();
                foreach (var t in tags) d[t.Key] = t.Value?.ToString() ?? "";
                lock (_seen) _seen.Add((v, d));
            });
            _l.Start();
        }

        public IReadOnlyList<(long Value, Dictionary<string, string> Tags)> Seen(string group)
        {
            lock (_seen) return [.. _seen.Where(s => s.Tags.GetValueOrDefault(InternalAuthGate.GroupTag) == group)];
        }

        public void Dispose() => _l.Dispose();
    }

    [Theory]
    [InlineData(null, HttpStatusCode.OK, "report")]
    [InlineData("Report", HttpStatusCode.OK, "report")]
    [InlineData("Enforce", HttpStatusCode.Unauthorized, "enforce")]
    public async Task 未認証の要求は群と段の属性つきで1件数え_段2では401(string? mode, HttpStatusCode expected, string modeTag)
    {
        using var probe = new Probe();
        await using var app = await StartAsync(mode);
        var client = app.GetTestClient();

        (await client.GetAsync("/g/x", TestContext.Current.CancellationToken)).StatusCode.Should().Be(expected);

        var m = probe.Seen("unit-group").Should().ContainSingle().Subject;
        m.Value.Should().Be(1);
        m.Tags[InternalAuthGate.ModeTag].Should().Be(modeTag);
    }

    [Fact]
    public async Task 認証済みの要求は数えずに通す()
    {
        using var probe = new Probe();
        await using var app = await StartAsync("Enforce");
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(UserHeader, "alice");

        (await client.GetAsync("/g/x", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        probe.Seen("unit-group").Should().BeEmpty();
    }

    [Fact]
    public async Task 不正な段の値では起動を止める()
    {
        var act = () => StartAsync("Enfroce");

        await act.Should().ThrowAsync<Exception>();
    }
}
