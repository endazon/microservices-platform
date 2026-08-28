using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Platform.Bff.Tests;

// NFR, ADR-0032, IADR-0251, #439 第 3 段(3b): **既定スキームを移すと Bearer 呼び出しがどうなるか**を測る。
//
// 🔴 **既存の BFF テストではこれを測れない。** `BffTestFactory` が既定スキームを `Test` へ
// 上書きしてしまうため、**「既定がどちらか」で挙動が変わる部分が丸ごと隠れる**。
// ここでは器を使わず、素の `TestServer` に 2 つのスキームを登録して**既定だけを変えて**比べる。
//
// 測っているのは**スキームの解決**であって、JWT の検証そのものではない
// （検証は `AddPlatformAuth` の責務で、本件の論点ではない）。
public class DefaultSchemeRoutingTests
{
    private const string CookieLike = "CookieLike";
    private const string BearerLike = "BearerLike";

    // 渡されたヘッダ／Cookie があれば認証成功、無ければ NoResult を返すだけの最小ハンドラ。
    private sealed class StubHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var ok = Scheme.Name switch
            {
                CookieLike => Request.Cookies.ContainsKey("session"),
                BearerLike => Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal),
                _ => false,
            };
            if (!ok) return Task.FromResult(AuthenticateResult.NoResult());

            var id = new ClaimsIdentity([new Claim(ClaimTypes.Name, Scheme.Name)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(id), Scheme.Name)));
        }
    }

    // 本番と同じ形: 振り分けスキームを既定に置き、Bearer が在れば Bearer 側・無ければ Cookie 側へ委ねる。
    private static async Task<IHost> HostWithSmartDefault()
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddAuthorization();
                    s.AddAuthentication("Smart")
                        .AddPolicyScheme("Smart", "Smart", o =>
                            o.ForwardDefaultSelector = ctx =>
                                ctx.Request.Headers.Authorization.ToString()
                                    .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                                    ? BearerLike : CookieLike)
                        .AddScheme<AuthenticationSchemeOptions, StubHandler>(CookieLike, _ => { })
                        .AddScheme<AuthenticationSchemeOptions, StubHandler>(BearerLike, _ => { });
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(e =>
                    {
                        // 既存の BFF 端点と同じ 2 形。**内側のポリシーはスキームを持たない。**
                        e.MapGet("/bff/thing", () => Results.Ok("ok")).RequireAuthorization();
                        e.MapGet("/bff/roled", () => Results.Ok("ok"))
                            .RequireAuthorization(p => p.RequireAuthenticatedUser());
                    });
                }))
            .StartAsync();
        return host;
    }

    // ── ［3b・案 B］3 点セット。**3 つ目が陰性対照**である。
    // 2 つだけだと「常に通す実装」が両方を通してしまう。

    // ★ 1: Cookie 呼び出しが通る。
    [Fact]
    public async Task Smart_default_accepts_cookie()
    {
        using var host = await HostWithSmartDefault();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("Cookie", "session=abc");

        (await client.GetAsync("/bff/thing", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ★ 2: Bearer 呼び出しも通る（統合スタックの外形確認を失わない）。
    [Fact]
    public async Task Smart_default_accepts_bearer()
    {
        using var host = await HostWithSmartDefault();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "t");

        (await client.GetAsync("/bff/thing", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 🔴 ★ 3（**陰性対照**）: **どちらも無ければ 401。**
    // これが無いと「常に通す」実装が 1・2 の両方を満たしてしまう。
    [Fact]
    public async Task Smart_default_rejects_when_neither_credential_is_present()
    {
        using var host = await HostWithSmartDefault();

        (await host.GetTestClient().GetAsync("/bff/thing", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // 🔴 ★ 条件 4: **Bearer 経路が認可を迂回していない。**
    // ロール要求を持つ端点（内側のポリシーがスキームを持たない形）でも、
    // **両方の経路が同じ判定を通る**こと。片方だけ認可が効かない形にしない。
    [Theory]
    [InlineData(true)]   // Bearer 経路
    [InlineData(false)]  // Cookie 経路
    public async Task Both_paths_go_through_the_same_authorization_on_roled_endpoints(bool useBearer)
    {
        using var host = await HostWithSmartDefault();
        var client = host.GetTestClient();
        if (useBearer)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "t");
        else
            client.DefaultRequestHeaders.Add("Cookie", "session=abc");

        (await client.GetAsync("/bff/roled", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ★ 陰性対照（条件 4 の裏）: 資格情報が無ければ、ロール端点も 401 である。
    [Fact]
    public async Task Roled_endpoint_rejects_without_any_credential()
    {
        using var host = await HostWithSmartDefault();

        (await host.GetTestClient().GetAsync("/bff/roled", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<IHost> HostWithDefault(string defaultScheme)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddAuthorization();
                    s.AddAuthentication(defaultScheme)
                        .AddScheme<AuthenticationSchemeOptions, StubHandler>(CookieLike, _ => { })
                        .AddScheme<AuthenticationSchemeOptions, StubHandler>(BearerLike, _ => { });
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(e =>
                        // 既存の BFF 端点と同じ形: **スキームを指定しない** `RequireAuthorization()`。
                        e.MapGet("/bff/thing", () => Results.Ok("ok")).RequireAuthorization());
                }))
            .StartAsync();
        return host;
    }

    // ★ 対照: 既定が Bearer 側なら、Bearer 呼び出しは通る（3a までの姿）。
    [Fact]
    public async Task Bearer_call_succeeds_while_bearer_is_the_default()
    {
        using var host = await HostWithDefault(BearerLike);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "t");

        var resp = await client.GetAsync("/bff/thing", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 🔴 ★ **既定を Cookie 側へ移すと、スキーム未指定の端点は Bearer 呼び出しを拒む。**
    //
    // これが 3b ① の副作用である。`scripts/verify-oidc-edge-flow.sh` は `/bff/*` を
    // **Bearer で 4 箇所**叩いており（実測）、既定を移すとそれらが 401 になる。
    // **ブラウザにトークンを出さない、という要件とは別の話**である（非ブラウザの呼び出し口の話）。
    [Fact]
    public async Task Bearer_call_is_rejected_once_the_cookie_scheme_becomes_the_default()
    {
        using var host = await HostWithDefault(CookieLike);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "t");

        var resp = await client.GetAsync("/bff/thing", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ★ 陽性対照: 既定が Cookie 側なら Cookie 呼び出しは通る（＝拒否が「何でも 401」ではない）。
    [Fact]
    public async Task Cookie_call_succeeds_once_the_cookie_scheme_becomes_the_default()
    {
        using var host = await HostWithDefault(CookieLike);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("Cookie", "session=abc");

        var resp = await client.GetAsync("/bff/thing", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
