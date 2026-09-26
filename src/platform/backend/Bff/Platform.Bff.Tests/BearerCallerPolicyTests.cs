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

// NFR-09, SC-13, ADR-0032, IADR-0251 決定 9, [[IADR-0429]] (#1393 / #1535):
// **`/bff/*` の Bearer 受理を「無人の主体（サービス間）」に絞ったことを固定する。**
//
// 🔴 **陰性対照が本体である。** 「サービス間 Bearer が通る」だけを測ると、
// **門を丸ごと外した実装（＝#1393 以前の姿）でも緑になる。** 通らないことを測る側が主役で、
// 陽性はその拒否が「何でも 401」ではないことの対照として置く。
//
// 🔴 #1535: **BFF 自身の confidential client（`bff`）名義の利用者トークンも通さない。**
// #1393 は非ブラウザの外形確認（`verify-oidc-edge-flow.sh`）のためにこの腕を残していたが、
// 同スクリプトはセッション Cookie へ移った。腕を戻す変異で落ちるのが
// `User_token_minted_for_the_bff_confidential_client_is_refused` と配線の同名試験である。
public class BearerCallerPolicyTests
{
    private const string BffClientId = "bff";

    private static ClaimsPrincipal Token(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Bearer"));

    /// <summary>認証されていない主体（`AuthenticationType` を持たない ClaimsIdentity）。</summary>
    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    // ── 🔴 陰性対照（NFR-09 テスト仕様 T-24） ────────────────────────────────────────────────────────────

    // ★ #1393 の受け入れ基準: **SPA 名義（public client）の利用者トークンは通らない。**
    // 撤去した `platform-spa` は「ブラウザが取得できる利用者トークン」の代表例である。
    [Fact]
    public void Public_client_user_token_is_refused()
    {
        BearerCallerPolicy.IsAcceptedCaller(
            Token(("azp", "platform-spa"), ("preferred_username", "developer")))
            .Should().BeFalse("ブラウザが public client で取れるトークンで /bff/* を直接叩けてはならない");
    }

    // ★ public client に限らない。**ブラウザ OIDC を持つ他ツールの利用者トークンも通らない**
    // （realm には grafana / argocd / vault / headlamp / wiki-js が居る）。
    [Theory]
    [InlineData("grafana")]
    [InlineData("argocd")]
    [InlineData("headlamp")]
    [InlineData("wiki-js")]
    public void User_tokens_minted_for_other_browser_clients_are_refused(string azp)
    {
        BearerCallerPolicy.IsAcceptedCaller(
            Token(("azp", azp), ("preferred_username", "developer")))
            .Should().BeFalse();
    }

    // 🔴 ★ #1535 の受け入れ基準: **BFF 自身の confidential client 名義の利用者トークンも通らない。**
    // client_secret を持つ者（旧 `verify-oidc-edge-flow.sh` の自前交換）なら取得できる形であり、
    // 利用者の資格情報で `/bff/*` を Bearer で叩ける口だった。利用者はセッション Cookie で入る。
    [Fact]
    public void User_token_minted_for_the_bff_confidential_client_is_refused()
    {
        BearerCallerPolicy.IsAcceptedCaller(
            Token(("azp", BffClientId), ("preferred_username", "developer")))
            .Should().BeFalse("利用者の資格情報で /bff/* に入る口はセッション Cookie だけである");
    }

    // ★ 未認証は通さない（主体が決まらないものを機械と読まない）。
    [Fact]
    public void Unauthenticated_principal_is_refused()
    {
        BearerCallerPolicy.IsAcceptedCaller(Anonymous()).Should().BeFalse();
        BearerCallerPolicy.IsAcceptedCaller(null).Should().BeFalse();
    }

    // ★ `azp` を持たない利用者トークン（クライアント識別が無い）も通らない。
    [Fact]
    public void User_token_without_a_client_claim_is_refused()
    {
        BearerCallerPolicy.IsAcceptedCaller(Token(("preferred_username", "developer")))
            .Should().BeFalse();
    }

    // ── 陽性対照（T-25。拒否が「何でも false」ではないこと） ──────────────────────────

    // ★ サービスアカウント（client credentials）は通る。サービス間 Bearer を壊さない。
    [Fact]
    public void Service_account_token_is_accepted()
    {
        BearerCallerPolicy.IsAcceptedCaller(
            Token(("azp", "retrieval-service"),
                  ("preferred_username", "service-account-retrieval-service")))
            .Should().BeTrue();
    }

    // ★ 第 2 形: `profile` スコープを持たない機械クライアント（利用者名が無い）。
    // 合成監視（`synthetic-monitor`。`/bff/analysis/ask` を client credentials で叩く）はここで通る。
    [Fact]
    public void Machine_client_without_a_username_is_accepted()
    {
        BearerCallerPolicy.IsAcceptedCaller(Token(("azp", "synthetic-monitor")))
            .Should().BeTrue();
    }

    // ★ #1535: 拒否は「`azp` が `bff` だから」ではなく「利用者だから」であること。
    // **同じ `azp=bff` でも、BFF 自身の service account（client credentials）は無人の主体として通る。**
    // これが無いと「`azp=bff` を一律に落とす」誤った実装でも陰性対照が緑になる。
    [Fact]
    public void Bff_own_service_account_token_is_accepted()
    {
        BearerCallerPolicy.IsAcceptedCaller(
            Token(("azp", BffClientId), ("preferred_username", "service-account-bff")))
            .Should().BeTrue();
    }

    // ── 配線（T-24 / T-25。純粋関数が実際に JwtBearer の腕へ掛かっていること） ────────────────
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

    private static async Task<TokenValidatedContext> RunOnTokenValidated(
        ClaimsPrincipal principal, string clientId = BffClientId)
    {
        var options = BearerOptions(clientId);
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

    // 🔴 ★ #1535 配線の陰性対照: BFF 自身の client 名義の利用者トークンもハンドラの側で失敗になる。
    // **構成の ClientId が何であっても**（空・既定・利用者トークンの `azp` と一致）拒否は変わらない ——
    // 受理の判定は構成値に依存しない（許可リストを構成で持たない。IADR-0420 と同じ理由）。
    [Theory]
    [InlineData(BffClientId)]
    [InlineData("")]
    public async Task Wiring_fails_authentication_for_a_bff_client_user_token(string configuredClientId)
    {
        var ctx = await RunOnTokenValidated(
            Token(("azp", BffClientId), ("preferred_username", "developer")), configuredClientId);

        ctx.Result.Should().NotBeNull("利用者のトークンは azp に関わらず認証を失敗させる");
        ctx.Result!.Succeeded.Should().BeFalse();
        ctx.Result.Failure!.Message.Should().Contain("セッション Cookie");
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
