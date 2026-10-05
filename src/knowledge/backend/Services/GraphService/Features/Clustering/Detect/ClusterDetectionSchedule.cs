namespace GraphService.Features.Clustering.Detect;

// FR-17, ADR-0035 決定 3, [[IADR-0496]] 決定 2・4 (#1733): 日次の検出が**いつ期限を迎えるか**（純関数）。
//
// 位相は**前回の成功**に付く。プロセスの起動には付かない —— 起動に付けると（従前の `PeriodicTimer`）、
// 24 時間より短い間隔で作り直される Pod では拍が永久に来ない（#1733: 9/27 から 1 度も走らなかった）。
public static class ClusterDetectionSchedule
{
    // 記録が「今」より先にあってよい幅（時計のずれの許容）。これを超えて未来の記録は**壊れた記録**として扱う。
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    // 成功の記録から見た、次の検出まで待つ長さ。`TimeSpan.Zero` なら今すぐ走らせる。
    //
    // - 記録が無い（初めての配備・表を足した直後）: すぐ。
    // - 前回の成功から `interval` 以上たっている: すぐ（取りこぼしを追いつく。何周期ぶん遅れていても 1 回だけ走る）。
    // - それ未満: 残りだけ待つ（1 周期で頭打ち。許容内の未来の記録でも 1 周期より長くは眠らない）。
    // - 🔴 許容（5 分）を超えて未来の記録: **すぐ走らせる。** 走れば今の時刻で上書きされ、記録が正される。
    //   「1 周期で頭打ちにして眠る」だと、起きてもまだ未来なので眠り直し、3 日先の記録なら 4 日走らない。
    public static TimeSpan DelayUntilDue(DateTimeOffset now, DateTimeOffset? lastSucceededAt, TimeSpan interval)
    {
        if (lastSucceededAt is not { } last || last > now + FutureTolerance)
            return TimeSpan.Zero;

        var remaining = last + interval - now;
        if (remaining <= TimeSpan.Zero)
            return TimeSpan.Zero;

        return remaining > interval ? interval : remaining;
    }

    // 連続 `failures` 回失敗した後の待ち。再試行の待ちを倍々にし、1 周期で頭打ちにする（1 回目 = 再試行の待ち）。
    public static TimeSpan Backoff(TimeSpan retryDelay, int failures, TimeSpan interval)
    {
        if (failures <= 1)
            return retryDelay < interval ? retryDelay : interval;

        var delay = retryDelay;
        for (var i = 1; i < failures && delay < interval; i++)
            delay += delay;
        return delay < interval ? delay : interval;
    }

    // 永続化された実行の記録から見た、次の試行まで待つ長さ（[[IADR-0496]] 決定 4）。
    //
    // 成功の期限（`DelayUntilDue`）に加え、**成功していない試行が残っている**（前の試行が例外・取り消し・
    // プロセスごと落ちた）なら、最後の試行から指数バックオフの分だけ待つ。長い方を採る。
    // 試行の記録が許容を超えて未来なら、バックオフは無視する（壊れた記録で眠り続けない）。
    public static TimeSpan NextDelay(
        DateTimeOffset now, ClusterDetectionRunState state, TimeSpan interval, TimeSpan retryDelay)
    {
        var wait = DelayUntilDue(now, state.LastSucceededAt, interval);
        if (state.AttemptsSinceSuccess > 0
            && state.LastAttemptedAt is { } attempted
            && attempted <= now + FutureTolerance)
        {
            var backoff = attempted + Backoff(retryDelay, state.AttemptsSinceSuccess, interval) - now;
            if (backoff > interval)
                backoff = interval;
            if (backoff > wait)
                wait = backoff;
        }

        return wait;
    }
}

// [[IADR-0496]]: `graph_batch_runs` の検出の行を読んだもの。行が無ければ `None`。
public sealed record ClusterDetectionRunState(
    DateTimeOffset? LastSucceededAt, DateTimeOffset? LastAttemptedAt, int AttemptsSinceSuccess)
{
    public static readonly ClusterDetectionRunState None = new(null, null, 0);
}
