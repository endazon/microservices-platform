using AwesomeAssertions;
using GraphService.Features.Clustering.Detect;

namespace GraphService.Tests.Features.Clustering;

// FR-17, ADR-0035 決定 3, [[IADR-0496]] 決定 2 (#1733, T-70): 日次の検出の期限の計算（純関数）。
//
// 🔴 位相は**前回の成功**に付く。起動に付けると、24 時間より短い間隔で作り直される Pod では
// 検出が永久に走らない（#1733。従前の `PeriodicTimer(1 日)`）。
[Trait("TestKind", "Unit")]
public sealed class ClusterDetectionScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    [Fact]
    public void 記録が無ければすぐ走らせる()
        => ClusterDetectionSchedule.DelayUntilDue(Now, null, Day).Should().Be(TimeSpan.Zero);

    // 取りこぼしを追いつく。何周期ぶん遅れていても待たない（#1733 の 9/27 → 10/3 は 6 周期遅れ）。
    [Theory]
    [InlineData(24 * 60)]
    [InlineData(25 * 60)]
    [InlineData(6 * 24 * 60)]
    public void 前回の成功から1周期以上たっていればすぐ走らせる(int minutesAgo)
        => ClusterDetectionSchedule.DelayUntilDue(Now, Now.AddMinutes(-minutesAgo), Day)
            .Should().Be(TimeSpan.Zero);

    // 陽性対照: 1 周期未満なら**残りだけ**待つ（起動からの 24 時間ではない）。
    [Theory]
    [InlineData(60, 23 * 60)]
    [InlineData(23 * 60 + 59, 1)]
    [InlineData(0, 24 * 60)]
    public void 前回の成功から1周期未満なら残りだけ待つ(int minutesAgo, int expectedMinutes)
        => ClusterDetectionSchedule.DelayUntilDue(Now, Now.AddMinutes(-minutesAgo), Day)
            .Should().Be(TimeSpan.FromMinutes(expectedMinutes));

    // 時計のずれで記録が未来にあっても、ずれの分だけ眠り続けない（1 周期で頭打ち）。
    [Fact]
    public void 未来の記録は1周期で頭打ちにする()
        => ClusterDetectionSchedule.DelayUntilDue(Now, Now.AddDays(3), Day).Should().Be(Day);
}
