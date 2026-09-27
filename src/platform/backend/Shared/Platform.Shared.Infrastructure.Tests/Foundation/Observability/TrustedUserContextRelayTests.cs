using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Observability;

// NFR-09, 計画 ADR-0086 決定 1・§結果, ADR-0119 決定 3 (#1636):
// **east-west gRPC の本文の利用者文脈を信じる中継者の許可集合**の共有 3 関数（判定・既定の解決・構成の形の検査）。
// 面ごとの束縛と統合は各サービスの `*RelayOptionsTests` / `*TrustedRelayTests` が見る。ここは関数そのものの形を固定する。
[Trait("TestKind", "Unit")]
public class TrustedUserContextRelayTests
{
    private static readonly IReadOnlyList<string> Defaults = ["relay-a"];

    private static ClaimsPrincipal Principal(string? username, string? azp)
    {
        var claims = new List<Claim>();
        if (username is not null) claims.Add(new Claim("preferred_username", username));
        if (azp is not null) claims.Add(new Claim("azp", azp));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", "preferred_username", ClaimTypes.Role));
    }

    [Fact]
    public void 未構成なら既定で_構成すると置き換え_空白は捨てる()
    {
        TrustedUserContextRelay.Effective(null, Defaults).Should().Equal("relay-a");
        TrustedUserContextRelay.Effective([" relay-b ", "", "  "], Defaults).Should().Equal("relay-b");
        TrustedUserContextRelay.Effective(["  "], Defaults).Should().BeEmpty("空白だけなら誰も信じない（既定へ戻さない）");
    }

    [Fact]
    public void 機械の主体のクライアント識別が序数一致で在るときだけ信じる()
    {
        TrustedUserContextRelay.Trusts(Principal("service-account-relay-a", "relay-a"), Defaults).Should().BeTrue();
        TrustedUserContextRelay.Trusts(Principal(null, "relay-a"), Defaults).Should().BeTrue("azp だけの機械クライアント");
        TrustedUserContextRelay.Trusts(Principal("service-account-relay-a", null), Defaults).Should().BeTrue("利用者名から復元");

        TrustedUserContextRelay.Trusts(Principal(null, "RELAY-A"), Defaults).Should().BeFalse("大小文字を畳まない");
        TrustedUserContextRelay.Trusts(Principal(null, "relay-a-x"), Defaults).Should().BeFalse("接頭辞一致にしない");
        TrustedUserContextRelay.Trusts(Principal(null, "relay-"), Defaults).Should().BeFalse("接頭辞一致にしない");
        TrustedUserContextRelay.Trusts(Principal("service-account-relay-a", "other"), Defaults).Should().BeFalse("azp が第一");
        TrustedUserContextRelay.Trusts(Principal("carol", "relay-a"), Defaults).Should().BeFalse("人のトークン");
        TrustedUserContextRelay.Trusts(new ClaimsPrincipal(new ClaimsIdentity()), Defaults).Should().BeFalse("未認証");
        TrustedUserContextRelay.Trusts(null, Defaults).Should().BeFalse();
        TrustedUserContextRelay.Trusts(Principal(null, "relay-a"), []).Should().BeFalse("空の集合は誰も信じない");
    }

    [Fact]
    public void 一つの値の構成は例外で_配列と未構成は通る()
    {
        var scalar = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Face:TrustedUserContextClients"] = "a,b" }).Build();
        var act = () => TrustedUserContextRelay.ThrowIfScalar(scalar, "Face", "relay-a");
        act.Should().Throw<InvalidOperationException>().WithMessage("*Face__TrustedUserContextClients__0=relay-a*");

        TrustedUserContextRelay.ThrowIfScalar(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Face:TrustedUserContextClients:0"] = "a" }).Build(), "Face", "relay-a");
        TrustedUserContextRelay.ThrowIfScalar(new ConfigurationBuilder().Build(), "Face", "relay-a");
    }
}
