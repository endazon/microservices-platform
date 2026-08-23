using AwesomeAssertions;
using Platform.Bff.Foundation.Session;
using System.Globalization;

namespace Platform.Bff.Tests;

// NFR, ADR-0032, IADR-0273 決定 3, #439: refresh 要否の判定（純関数）。
// 通し（refresh 成功／拒否 → 即時失効）は BffSessionFlowTests が受け持つ。
public class SessionTokenRefresherTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private static string At(TimeSpan offset) =>
        (Now + offset).ToString("o", CultureInfo.InvariantCulture);

    // ★ 陽性: 期限切れ・スキュー圏内は refresh する。
    [Fact]
    public void Expired_or_nearly_expired_tokens_need_a_refresh()
    {
        SessionTokenRefresher.NeedsRefresh(At(TimeSpan.FromMinutes(-5)), Now).Should().BeTrue();
        SessionTokenRefresher.NeedsRefresh(At(TimeSpan.FromSeconds(30)), Now)
            .Should().BeTrue("スキュー（60 秒）圏内は先回りして更新する");
    }

    // ★ 陰性: まだ生きているトークンは refresh しない（毎リクエスト token endpoint を叩く実装を落とす）。
    [Fact]
    public void Live_tokens_do_not_need_a_refresh()
        => SessionTokenRefresher.NeedsRefresh(At(TimeSpan.FromMinutes(4)), Now).Should().BeFalse();

    // ★ expires_at を持たないチケット（トークンを伴わないサインイン）は refresh の対象外。
    // ここが true になると、トークン無しの正当なセッションが全部殺される。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-date")]
    public void Tickets_without_a_parseable_expiry_are_left_alone(string? expiresAt)
        => SessionTokenRefresher.NeedsRefresh(expiresAt, Now).Should().BeFalse();
}
