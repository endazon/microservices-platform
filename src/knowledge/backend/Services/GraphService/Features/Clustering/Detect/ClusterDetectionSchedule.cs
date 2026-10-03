namespace GraphService.Features.Clustering.Detect;

// FR-17, ADR-0035 決定 3, [[IADR-0496]] 決定 2 (#1733): 日次の検出が**いつ期限を迎えるか**（純関数）。
//
// 位相は**前回の成功**に付く。プロセスの起動には付かない —— 起動に付けると（従前の `PeriodicTimer`）、
// 24 時間より短い間隔で作り直される Pod では拍が永久に来ない（#1733: 9/27 から 1 度も走らなかった）。
public static class ClusterDetectionSchedule
{
    // 次の検出まで待つ長さ。`TimeSpan.Zero` なら今すぐ走らせる。
    //
    // - 記録が無い（初めての配備・表を足した直後）: すぐ。
    // - 前回の成功から `interval` 以上たっている: すぐ（取りこぼしを追いつく。何周期ぶん遅れていても 1 回だけ走る）。
    // - それ未満: 残りだけ待つ。
    // - 前回の成功が未来にある（時計のずれ）: 1 周期で頭打ちにする（ずれの分だけ眠り続けない）。
    public static TimeSpan DelayUntilDue(DateTimeOffset now, DateTimeOffset? lastSucceededAt, TimeSpan interval)
    {
        if (lastSucceededAt is not { } last)
            return TimeSpan.Zero;

        var remaining = last + interval - now;
        if (remaining <= TimeSpan.Zero)
            return TimeSpan.Zero;

        return remaining > interval ? interval : remaining;
    }
}
