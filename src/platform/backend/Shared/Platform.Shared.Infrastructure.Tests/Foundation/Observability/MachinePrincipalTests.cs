using System.Security.Claims;
using AwesomeAssertions;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Observability;

// FR-05, FR-09, SC-12, ADR-0036, ADR-0062, ADR-0085 決定 4, [[IADR-0420]] (#1233):
// **「ユニットの主体（無人主体）か」の判定規則。**
//
// 🔴 **陽性対照が本体である。** 「無人主体を無人と読む」だけなら、
// 「クライアント識別クレームを持つか」で書いても緑になる —— しかし `azp` は
// **人間のトークンにも必ず付く**ので、その実装は**全利用者を無人主体にする。**
// 落とせるのは「対話ログインした人間は無人ではない」を測る試験だけである。
[Trait("TestKind", "Unit")]
public class MachinePrincipalTests
{
    private const string KbWriter = "ai-stock-trading-kb-writer";

    private static ClaimsPrincipal Authenticated(params Claim[] claims)
        => new(new ClaimsIdentity(claims, "Test"));

    // ★ 腕 A: Keycloak は client credentials の主体へ
    // `preferred_username = service-account-<clientId>` を発行する。
    [Fact]
    public void サービスアカウントのpreferred_usernameは無人主体と判定する()
        => MachinePrincipal.IsMachine(Authenticated(
                new Claim("preferred_username", $"service-account-{KbWriter}"),
                new Claim("azp", KbWriter)))
            .Should().BeTrue();

    // ★ 腕 B: `profile` スコープを持たない機械クライアントは利用者名を発行しない。
    [Fact]
    public void 利用者名を持たずクライアント識別だけを持つ主体は無人主体と判定する()
        => MachinePrincipal.IsMachine(Authenticated(new Claim("azp", KbWriter)))
            .Should().BeTrue();

    // 🔴 ★ **陽性対照 1**: 対話ログインした人間は無人主体ではない。
    // **`azp` を持っていてもである** —— SPA の clientId が必ず入る。
    // 「クライアント識別クレームを持つか」だけで書いた実装はここで落ちる。
    [Fact]
    public void 対話ログインの利用者はazpを持っていても無人主体と判定しない()
        => MachinePrincipal.IsMachine(Authenticated(
                new Claim("preferred_username", "hanako"),
                new Claim("azp", "platform-spa")))
            .Should().BeFalse();

    // 🔴 ★ **陽性対照 2**: 名前が `service-account-` を**含む**だけの利用者は無人主体ではない
    // （判定は接頭辞であって部分一致ではない）。
    [Fact]
    public void 名前にservice_accountを含むだけの利用者は無人主体と判定しない()
        => MachinePrincipal.IsMachine(Authenticated(
                new Claim("preferred_username", "not-a-service-account-user")))
            .Should().BeFalse();

    // ★ 未認証は無人主体ではない（主体が決まらないものを機械と読まない）。
    [Fact]
    public void 未認証は無人主体と判定しない()
        => MachinePrincipal.IsMachine(new ClaimsPrincipal(new ClaimsIdentity())).Should().BeFalse();

    // ★ null も同じ（呼び出し側で分岐させない）。
    [Fact]
    public void nullは無人主体と判定しない()
        => MachinePrincipal.IsMachine(null).Should().BeFalse();

    // ★ `Identity.Name` 経由でも読める（`AddPlatformAuth` は NameClaimType を
    // `preferred_username` に設定するが、テスト用ハンドラは `ClaimTypes.Name` を使う）。
    [Fact]
    public void ClaimTypesNameのサービスアカウント名でも無人主体と判定する()
        => MachinePrincipal.IsMachine(Authenticated(
                new Claim(ClaimTypes.Name, $"service-account-{KbWriter}")))
            .Should().BeTrue();

    // ★ 属性に載せる値は `azp` の生値である。
    [Fact]
    public void クライアント識別子はazpの生値を返す()
        => MachinePrincipal.ClientIdOf(Authenticated(
                new Claim("preferred_username", $"service-account-{KbWriter}"),
                new Claim("azp", KbWriter)))
            .Should().Be(KbWriter);

    // ★ `azp` が無ければ `service-account-<clientId>` から復元する
    // （realm の設定次第で `azp` が落ちても、属性が `unknown` へ潰れない）。
    [Fact]
    public void azpが無ければサービスアカウント名からクライアント識別子を復元する()
        => MachinePrincipal.ClientIdOf(Authenticated(
                new Claim("preferred_username", $"service-account-{KbWriter}")))
            .Should().Be(KbWriter);

    // ★ どちらも無ければ null（呼び出し側が既定値へ倒す）。
    [Fact]
    public void クライアント識別子が無ければnullを返す()
        => MachinePrincipal.ClientIdOf(Authenticated(new Claim("preferred_username", "hanako")))
            .Should().BeNull();
}
