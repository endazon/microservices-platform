using AwesomeAssertions;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Extensions;

// NFR-18, IADR-0686 (#2432・AI レビュー): 利用者ごとの直近 60 秒の窓。
// 🔴 段 1（Report）でも、超えた要求は記録しない（段 2 で拒んだ要求と同じ扱い）。記録は上限の件数で頭打ちになり、
// 超過の判定（どの要求が超えたか）は段 1 と段 2 で変わらない。
[Trait("TestKind", "Unit")]
public class UserRateWindowTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);

    private static List<bool> Run(UserRateWindow w, int limit, IEnumerable<TimeSpan> offsets)
        => [.. offsets.Select(o => w.CountAndCheck("alice", T0 + o, limit))];

    [Fact]
    public void 超えた後も要求を送り続けても記録は上限の件数で頭打ちになる()
    {
        var w = new UserRateWindow();

        for (var i = 0; i < 1000; i++)
            w.CountAndCheck("alice", T0 + TimeSpan.FromMilliseconds(i), limit: 3);

        w.Tracked("alice").Should().Be(3);
    }

    [Fact]
    public void 超過の判定は窓が流れても段2と同じ_拒んだ要求を数えないので窓の後に枠が空く()
    {
        // 上限 2。0 秒・1 秒・2 秒・3 秒に 4 件、60.5 秒・61.5 秒に 2 件。
        // 段 2 の意味（超えた要求は数えない）: 0・1 秒が枠を使い、2・3 秒は超過。60.5 秒には 0 秒の分が窓から出るので通る。
        var offsets = new[] { 0.0, 1, 2, 3, 60.5, 61.5 }.Select(TimeSpan.FromSeconds);

        Run(new UserRateWindow(), 2, offsets).Should().Equal(false, false, true, true, false, false);
    }

    [Fact]
    public void 利用者ごとに別の窓を持つ()
    {
        var w = new UserRateWindow();
        w.CountAndCheck("alice", T0, 1).Should().BeFalse();
        w.CountAndCheck("alice", T0, 1).Should().BeTrue();
        w.CountAndCheck("bob", T0, 1).Should().BeFalse();
    }
}
