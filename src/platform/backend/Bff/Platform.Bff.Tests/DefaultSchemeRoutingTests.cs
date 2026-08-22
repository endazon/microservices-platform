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

        var resp = await client.GetAsync("/bff/thing");

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

        var resp = await client.GetAsync("/bff/thing");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ★ 陽性対照: 既定が Cookie 側なら Cookie 呼び出しは通る（＝拒否が「何でも 401」ではない）。
    [Fact]
    public async Task Cookie_call_succeeds_once_the_cookie_scheme_becomes_the_default()
    {
        using var host = await HostWithDefault(CookieLike);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("Cookie", "session=abc");

        var resp = await client.GetAsync("/bff/thing");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
