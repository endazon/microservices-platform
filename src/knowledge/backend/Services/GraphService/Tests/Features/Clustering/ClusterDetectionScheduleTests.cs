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

    // ［#1733 監査］許容（5 分）を超えて未来の記録は壊れた記録として**すぐ走らせる**（走れば今の時刻で上書きされる）。
    // 「1 周期で頭打ちにして眠る」だと、起きてもまだ未来なので眠り直し、3 日先の記録なら 4 日走らない。
    [Theory]
    [InlineData(6)]
    [InlineData(3 * 24 * 60)]
    public void 許容を超えて未来の記録はすぐ走らせる(int minutesAhead)
        => ClusterDetectionSchedule.DelayUntilDue(Now, Now.AddMinutes(minutesAhead), Day).Should().Be(TimeSpan.Zero);

    // 許容内（時計の小さなずれ）は期限内として扱い、1 周期で頭打ちにする。
    [Fact]
    public void 許容内の未来の記録は1周期で頭打ちにする()
        => ClusterDetectionSchedule.DelayUntilDue(Now, Now.AddMinutes(3), Day).Should().Be(Day);

    // T-77: 連続した失敗の待ちは再試行の待ちの倍々で、1 周期で頭打ち。
    [Theory]
    [InlineData(1, 60)]
    [InlineData(2, 120)]
    [InlineData(3, 240)]
    [InlineData(5, 960)]
    [InlineData(6, 24 * 60)]
    [InlineData(40, 24 * 60)]
    public void 失敗の待ちは倍々で1周期が上限(int failures, int expectedMinutes)
        => ClusterDetectionSchedule.Backoff(TimeSpan.FromHours(1), failures, Day)
            .Should().Be(TimeSpan.FromMinutes(expectedMinutes));

    // T-77: 成功していない試行が残っていれば、成功の期限と最後の試行からのバックオフの長い方を待つ。
    [Fact]
    public void 成功していない試行があれば最後の試行からバックオフを待つ()
    {
        var state = new ClusterDetectionRunState(Now.AddDays(-2), Now.AddMinutes(-30), AttemptsSinceSuccess: 3);
        ClusterDetectionSchedule.NextDelay(Now, state, Day, TimeSpan.FromHours(1))
            .Should().Be(TimeSpan.FromHours(4) - TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void 成功していない試行が無ければ成功の期限だけを見る()
    {
        var state = new ClusterDetectionRunState(Now.AddHours(-1), Now.AddHours(-1), AttemptsSinceSuccess: 0);
        ClusterDetectionSchedule.NextDelay(Now, state, Day, TimeSpan.FromHours(1)).Should().Be(TimeSpan.FromHours(23));
    }

    // 試行の記録が許容を超えて未来なら、バックオフで眠り続けない（壊れた記録）。
    [Fact]
    public void 未来の試行の記録ではバックオフしない()
    {
        var state = new ClusterDetectionRunState(null, Now.AddDays(3), AttemptsSinceSuccess: 2);
        ClusterDetectionSchedule.NextDelay(Now, state, Day, TimeSpan.FromHours(1)).Should().Be(TimeSpan.Zero);
    }
}
