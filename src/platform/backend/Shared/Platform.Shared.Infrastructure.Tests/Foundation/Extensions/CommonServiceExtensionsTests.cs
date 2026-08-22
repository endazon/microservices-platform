using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Extensions;

// NFR / #901: UsePlatformMiddleware が積む順序と構成を固定する。本番 10 箇所が使う。
// ミドルウェアの**順序**は、これまで散文以外のどこにも守られていなかった。
//   - CorrelationIdMiddleware が抜けると相関 ID の BeginScope が消え、ADR-0006 が静かに
//     成立しなくなる（**ログは出続ける**ので気付けない）
//   - UseAuthentication と UseAuthorization の順序が入れ替わると、認可必須の経路が
//     認証済みでも 401 になる
//
// 🔴 **順序の試験を書く根拠は事前実測である**（#901 作業仕様書）。
//    正順(Authn→Authz)=200 / 入替(Authz→Authn)=401 の差を測ってから書いた。
//    **ただしこの差は「認可を要求するエンドポイントが存在するとき」にのみ現れる。**
//    認可不要の経路しか無い構成では順序を入れ替えても素通りするため、
//    本試験は **認可を要求するエンドポイントを自分で建てて**測る。
public class CommonServiceExtensionsTests
{
    private const string Header = "X-Correlation-ID";

    /// <summary>常に認証成功するテスト用スキーム（順序の差を観測するために要る）。</summary>
    private sealed class AlwaysAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "Test");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services
            .AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, AlwaysAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UsePlatformMiddleware();
        app.MapGet("/open", () => "open");
        app.MapGet("/secure", () => "secure").RequireAuthorization();

        await app.StartAsync();
        return app;
    }

    // 試験 6: CorrelationIdMiddleware が積まれている（応答に相関 ID が現れる）。
    [Fact]
    public async Task 相関IDミドルウェアが積まれ応答に相関IDが載る()
    {
        await using var app = await StartAsync();

        var res = await app.GetTestClient().GetAsync("/open");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Headers.TryGetValues(Header, out var values).Should().BeTrue(
            "CorrelationIdMiddleware が抜けると相関 ID が消え、ADR-0006 が静かに成立しなくなる");
        values!.Single().Should().NotBeNullOrWhiteSpace();
    }

    // 試験 6b: 呼び出し側が相関 ID を渡した場合はそれを引き継ぐ（新規採番で上書きしない）。
    [Fact]
    public async Task 要求に相関IDがあればそれを引き継ぐ()
    {
        await using var app = await StartAsync();

        var client = app.GetTestClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/open");
        req.Headers.Add(Header, "given-correlation-id");
        var res = await client.SendAsync(req);

        res.Headers.GetValues(Header).Single().Should().Be("given-correlation-id");
    }

    // 試験 7: 認可必須の経路が認証済みで 200 になる（＝ Authentication が Authorization より先）。
    // 🔴 順序が入れ替わると HttpContext.User が未設定のまま認可が評価され 401 になる（事前実測済み）。
    [Fact]
    public async Task 認可必須の経路が認証済みで200になる_認証が認可より先に積まれている()
    {
        await using var app = await StartAsync();

        var res = await app.GetTestClient().GetAsync("/secure");

        res.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "UseAuthorization が UseAuthentication より先だと、User が未設定のまま認可が評価され 401 になる");
    }

    // 対照: 認可不要の経路は順序に関係なく通る。**試験 7 の差が「認可必須の経路」由来であることの固定**。
    // これが無いと、試験 7 が落ちたときに「認証が壊れた」のか「器が壊れた」のか切り分けられない。
    [Fact]
    public async Task 対照_認可不要の経路は順序に関係なく200になる()
    {
        await using var app = await StartAsync();

        (await app.GetTestClient().GetAsync("/open")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
