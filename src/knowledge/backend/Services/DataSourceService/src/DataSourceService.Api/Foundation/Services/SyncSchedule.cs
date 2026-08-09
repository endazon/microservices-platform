namespace DataSourceService.Api.Foundation.Services;

// FR-01, UC-04（基本 2: システムが定期的に原本を取得する）, SC-06, IADR-0136:
// 定期同期の「次回実行時刻」を答えるための位相（起点時刻＋実効間隔）を保持する singleton。
//
// 計画（planning#200 / 裁定 Q15）は「NextSyncAt は**共通間隔の次回実行時刻**として全ソース同じ値を返す」
// と定める。同期は DataSourceSyncHostedService が全ソース共通の間隔（DataSourceSync:IntervalSeconds）で
// 回すため、答えに必要なのは「間隔」と「位相（いつを起点に刻んでいるか）」の 2 つである。間隔は設定に
// あるが、位相はどこにも永続化されていない（PeriodicTimer はワーカーの起動時刻から刻むだけ）。
// そこでワーカーが起動時に起点を記録し、読み出し時に「現在より真に後の最初の境界」を計算する。
//
// インメモリで持つ理由: 次回時刻はワーカーの位相から**導出できる値**であって状態ではない。DB に持つと
// プロセス再起動のたびに実体とずれる。**同期健全性（連続失敗回数・直近エラー）とは扱いが逆である** ——
// あちらは導出できない事実なのでエンティティへ永続化する（#537 / IADR-0148）。
public sealed class SyncSchedule(TimeProvider timeProvider)
{
    // ワーカー起動時に 1 回だけ書き、以後は読むだけ。参照の差し替えは原子的なので volatile で足りる
    // （2 つのフィールドに分けると起点と間隔がちぐはぐに読まれ得る）。
    private volatile Cadence? _cadence;

    private sealed record Cadence(DateTimeOffset StartedAt, TimeSpan Interval);

    // 定期同期ワーカーが起動時に呼ぶ。interval は実効間隔（30 秒床を適用済みの値）。
    public void Start(TimeSpan interval)
    {
        // 0 以下だと次回時刻を刻めない（読み出し時の 0 除算になる）。呼び出し元の誤りを近くで知らせる。
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        _cadence = new Cadence(timeProvider.GetUtcNow(), interval);
    }

    // 次回の同期実行時刻。定期同期が動いていなければ null（＝次回は無い。無効時に嘘をつかない）。
    // 起点 + 間隔 × n のうち、現在時刻より**真に後**の最初のもの（境界ちょうどのときは次の境界）。
    public DateTimeOffset? NextRunAt
    {
        get
        {
            var cadence = _cadence;
            if (cadence is null) return null;

            var elapsed = timeProvider.GetUtcNow() - cadence.StartedAt;
            var elapsedIntervals = elapsed <= TimeSpan.Zero ? 0L : elapsed.Ticks / cadence.Interval.Ticks;
            return cadence.StartedAt + cadence.Interval * (elapsedIntervals + 1);
        }
    }
}
