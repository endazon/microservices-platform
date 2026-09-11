using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.Bff.Foundation.Session;
using System.Security.Claims;

namespace Platform.Bff.Tests;

// NFR, SC-13, ADR-0032, IADR-0251 決定 9, [[IADR-0429]] (#1393):
// **`/bff/*` の Bearer 受理を「ブラウザが取得し得ないトークン」に絞ったことを固定する。**
//
// 🔴 **陰性対照が本体である。** 「サービス間 Bearer が通る」だけを測ると、
// **門を丸ごと外した実装（＝従前の姿）でも緑になる。** 通らないことを測る側が主役で、
// 陽性はその拒否が「何でも 401」ではないことの対照として置く。
public class BearerCallerPolicyTests
{
    private const string BffClientId = "bff";

    private static ClaimsPrincipal Token(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Bearer"));

    /// <summary>認証されていない主体（`AuthenticationType` を持たない ClaimsIdentity）。</summary>
    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    // ── 🔴 陰性対照 ────────────────────────────────────────────────────────────

    // ★ 本 issue の受け入れ基準そのもの: **SPA 名義（public client）の利用者トークンは通らない。**
    // 撤去した `platform-spa` は「ブラウザが取得できる利用者トークン」の代表例である。
    [Fact]
    public void Public_client_user_token_is_refused()
    {
        BearerCallerPolicy.IsAcceptedCaller(
            Token(("azp", "platform-spa"), ("preferred_username", "developer")), BffClientId)
            .Should().BeFalse("ブラウザが public client で取れるトークンで /bff/* を直接叩けてはならない");
    }

    // ★ public client に限らない。**ブラウザ OIDC を持つ他ツールの利用者トークンも通らない**
    // （realm には grafana / argocd / minio / vault / headlamp / wiki-js が居る）。
    [Theory]
    [InlineData("grafana")]
    [InlineData("argocd")]
    [InlineData("headlamp")]
    [InlineData("wiki-js")]
    public void User_tokens_minted_for_other_browser_clients_are_refused(string azp)
    {
        BearerCallerPolicy.IsAcceptedCaller(
            Token(("azp", azp), ("preferred_username", "developer")), BffClientId)
            .Should().BeFalse();
    }

    // ★ 未認証は通さない（主体が決まらないものを機械と読まない）。
    [Fact]
    public void Unauthenticated_principal_is_refused()
    {
        BearerCallerPolicy.IsAcceptedCaller(Anonymous(), BffClientId).Should().BeFalse();
        BearerCallerPolicy.IsAcceptedCaller(null, BffClientId).Should().BeFalse();
    }

    // 🔴 ★ 構成の縮退で門が開かないこと: `ClientId` が空／未設定でも
    // 「何でも一致」へ倒れない（腕 B は空を一致と読まない）。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_bff_client_id_does_not_open_the_gate(string? clientId)
    {
        BearerCallerPolicy.IsAcceptedCaller(
            Token(("azp", "bff"), ("preferred_username", "developer")), clientId)
            .Should().BeFalse();
    }

    // ★ `azp` を持たない利用者トークン（クライアント識別が無い）も通らない。
    [Fact]
    public void User_token_without_a_client_claim_is_refused()
    {
        BearerCallerPolicy.IsAcceptedCaller(Token(("preferred_username", "developer")), BffClientId)
            .Should().BeFalse();
    }

    // ── 陽性対照（拒否が「何でも false」ではないこと） ──────────────────────────

    // ★ 腕 A: サービスアカウント（client credentials）は通る。サービス間 Bearer を壊さない。
    [Fact]
    public void Service_account_token_is_accepted()
    {
        BearerCallerPolicy.IsAcceptedCaller(
            Token(("azp", "retrieval-service"),
                  ("preferred_username", "service-account-retrieval-service")), BffClientId)
            .Should().BeTrue();
    }

    // ★ 腕 A の第 2 形: `profile` スコープを持たない機械クライアント（利用者名が無い）。
    [Fact]
    public void Machine_client_without_a_username_is_accepted()
    {
        BearerCallerPolicy.IsAcceptedCaller(Token(("azp", "synthetic-monitor")), BffClientId)
            .Should().BeTrue();
    }

    // ★ 腕 B: BFF 自身の confidential client 名義。**ブラウザは client_secret を持てないので
    // この名義のトークンを取得できない**（移行期に残す非ブラウザの外形確認のための腕）。
    [Fact]
    public void User_token_minted_for_the_bff_confidential_client_is_accepted()
    {
        BearerCallerPolicy.IsAcceptedCaller(
            Token(("azp", BffClientId), ("preferred_username", "developer")), BffClientId)
            .Should().BeTrue();
    }

    // ── 配線（純粋関数が実際に JwtBearer の腕へ掛かっていること） ────────────────
    //
    // 🔴 関数だけを測ると「関数は正しいが誰も呼んでいない」形が緑になる。
    // `AddBffSession` が構成した `JwtBearerOptions` の `OnTokenValidated` を実際に走らせる。

    private static JwtBearerOptions BearerOptions(string clientId = BffClientId)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BffSession:ClientId"] = clientId,
                ["BffSession:RedisConnectionString"] = "localhost:6379",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBffSession(config);
        using var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
                 .Get(JwtBearerDefaults.AuthenticationScheme);
    }

    private static async Task<TokenValidatedContext> RunOnTokenValidated(ClaimsPrincipal principal)
    {
        var options = BearerOptions();
        var ctx = new TokenValidatedContext(
            new DefaultHttpContext(),
            new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler)),
            options)
        {
            Principal = principal,
        };
        await options.Events!.OnTokenValidated(ctx);
        return ctx;
    }

    // 🔴 ★ 配線の陰性対照: public client 名義の利用者トークンは **ハンドラの側で失敗になる**（→ 401）。
    [Fact]
    public async Task Wiring_fails_authentication_for_a_public_client_user_token()
    {
        var ctx = await RunOnTokenValidated(
            Token(("azp", "platform-spa"), ("preferred_username", "developer")));

        ctx.Result.Should().NotBeNull("受理しない主体は認証を失敗させる（fail-closed）");
        ctx.Result!.Succeeded.Should().BeFalse();
        ctx.Result.Failure!.Message.Should().Contain("ADR-0032");
    }

    // ★ 配線の陽性対照: サービスアカウントは素通りする（`Result` を触らない）。
    [Fact]
    public async Task Wiring_leaves_service_account_tokens_untouched()
    {
        var ctx = await RunOnTokenValidated(
            Token(("azp", "retrieval-service"),
                  ("preferred_username", "service-account-retrieval-service")));

        ctx.Result.Should().BeNull("受理する主体では認証結果に手を触れない");
    }
}
