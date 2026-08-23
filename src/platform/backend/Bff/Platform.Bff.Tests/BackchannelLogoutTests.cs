using AwesomeAssertions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Platform.Bff.Foundation.Session;
using System.Net;

namespace Platform.Bff.Tests;

// NFR, ADR-0032, IADR-0273 決定 2, #439: バックチャネルログアウト＝「管理者による無効化 → 全セッション
// 即時失効」の経路。issue #439 が要求する一連（ログイン → 利用 → 無効化 → 即時失効）を、
// 認可サーバの代わりに**自前で署名した logout_token** を受け口へ届けて通しで固定する。
//
// 🔴 **陰性テストは陽性対照と対で置く。** 「常に 400 を返す実装」「常に 200 を返して何も消さない実装」
// のどちらも、片側だけのテストなら緑になってしまう。各陰性ケースは
// **(a) 400 が返ること** と **(b) セッションが生き残ること** の両方を確かめる。
public class BackchannelLogoutTests
{
    private const string Audience = "bff"; // BffSessionOptions.ClientId の既定（realm の bff クライアント）

    private static string LogoutToken(
        string? sub = "alice",
        string? issuer = SessionTestHost.Issuer,
        string audience = Audience,
        SecurityKey? key = null,
        bool withEvents = true,
        bool withNonce = false)
    {
        var claims = new Dictionary<string, object>();
        if (sub is not null) claims["sub"] = sub;
        claims["sid"] = "sess-1";
        claims["jti"] = Guid.NewGuid().ToString("N");
        if (withEvents)
            claims["events"] = new Dictionary<string, object>
            {
                [BackchannelLogoutProcessor.BackchannelLogoutEvent] = new Dictionary<string, object>(),
            };
        if (withNonce) claims["nonce"] = "n-1";

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddMinutes(2),
            Claims = claims,
            SigningCredentials = new SigningCredentials(
                key ?? SessionTestHost.SigningKey, SecurityAlgorithms.RsaSha256),
        });
    }

    private static Task<HttpResponseMessage> PostLogoutTokenAsync(SessionTestHost host, string token) =>
        host.Client.PostAsync(
            "/bff/auth/backchannel-logout",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = token }));

    // 🔴 ★ 本丸（陽性）: ログイン → 利用（200）→ 認可サーバがバックチャネルで失効 →
    // **同じ Cookie の次のリクエストが 401**。Cookie を運ばないサーバ間 POST で消えることが要点
    // （フレームワーク既定の remote-signout では**何も消えない**ことの裏返し）。
    [Fact]
    public async Task Valid_logout_token_revokes_all_sessions_of_the_subject_immediately()
    {
        await using var host = await SessionTestHost.StartAsync();
        var cookie1 = await host.SignInAsync("alice", sid: "sess-1");
        var cookie2 = await host.SignInAsync("alice", sid: "sess-2");
        (await host.Client.SendAsync(host.Request(HttpMethod.Get, "/bff/auth/me", cookie1)))
            .StatusCode.Should().Be(HttpStatusCode.OK, "失効前は通る（陽性対照）");

        var resp = await PostLogoutTokenAsync(host, LogoutToken(sub: "alice"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.Client.SendAsync(host.Request(HttpMethod.Get, "/bff/auth/me", cookie1)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "全セッションが即時失効する");
        (await host.Client.SendAsync(host.Request(HttpMethod.Get, "/bff/auth/me", cookie2)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "**全**セッション＝2 本目も消える");
    }

    // ★ 陰性（他人）: 別 subject のセッションは巻き込まれない（「全員を消す実装」を落とす対照。
    // RedisTicketStoreTests の変異試験で実在した壊れ方である）。
    [Fact]
    public async Task Logout_token_for_one_subject_does_not_touch_another()
    {
        await using var host = await SessionTestHost.StartAsync();
        var bobCookie = await host.SignInAsync("bob", sid: "sess-b");

        (await PostLogoutTokenAsync(host, LogoutToken(sub: "alice")))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await host.Client.SendAsync(host.Request(HttpMethod.Get, "/bff/auth/me", bobCookie)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    public static TheoryData<string, string> InvalidTokens => new()
    {
        { "wrong-signature", LogoutToken(key: SessionTestHost.NewRsaKey()) },
        { "wrong-issuer", LogoutToken(issuer: "https://evil.example/realms/platform") },
        { "wrong-audience", LogoutToken(audience: "another-client") },
        { "missing-events", LogoutToken(withEvents: false) },
        { "nonce-present", LogoutToken(withNonce: true) },
        { "missing-sub", LogoutToken(sub: null) },
        { "not-a-jwt", "garbage" },
    };

    // 🔴 ★ 陰性の対: 不正な logout_token は **(a) 400** かつ **(b) セッションを消さない**。
    // 署名・iss・aud を検証しない実装（＝第三者が任意の利用者を強制ログアウトできる穴）と、
    // events / nonce / sub の構造検査を省いた実装（ID トークンすり替え等）を両方落とす。
    [Theory]
    [MemberData(nameof(InvalidTokens))]
    public async Task Invalid_logout_tokens_are_rejected_and_revoke_nothing(string reason, string token)
    {
        await using var host = await SessionTestHost.StartAsync();
        var cookie = await host.SignInAsync("alice", sid: "sess-1");

        var resp = await PostLogoutTokenAsync(host, token);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "case: {0}", reason);
        (await host.Client.SendAsync(host.Request(HttpMethod.Get, "/bff/auth/me", cookie)))
            .StatusCode.Should().Be(HttpStatusCode.OK, "不正な token でセッションが消えてはならない（{0}）", reason);
    }

    // ── events の判定（純関数）: プロパティ名として持つことが条件。
    // 値の中に URL 文字列が現れるだけの JSON を「含む」と誤判定しない。

    [Fact]
    public void Events_json_with_the_logout_event_property_is_accepted()
        => BackchannelLogoutProcessor.HasLogoutEvent(
                "{\"" + BackchannelLogoutProcessor.BackchannelLogoutEvent + "\":{}}")
            .Should().BeTrue();

    [Theory]
    [InlineData("""{"other":"http://schemas.openid.net/event/backchannel-logout"}""")] // 値に URL があるだけ
    [InlineData("""{}""")]
    [InlineData("""["http://schemas.openid.net/event/backchannel-logout"]""")] // オブジェクトでない
    [InlineData("not-json")]
    public void Events_json_without_the_logout_event_property_is_rejected(string eventsJson)
        => BackchannelLogoutProcessor.HasLogoutEvent(eventsJson).Should().BeFalse();
}
