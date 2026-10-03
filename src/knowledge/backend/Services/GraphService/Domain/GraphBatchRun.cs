namespace GraphService.Domain;

// FR-17, ADR-0035 決定 3, [[IADR-0496]] (#1733): 定期バッチ 1 種につき 1 行の**実行の記録**。
//
// - `LastSucceededAt`: 最後に**成功した**周期の開始時刻。🔴 **成功した周期だけが書く。** 書き手はバッチの本体であり、
//   本体の書き込みと**同じ `SaveChanges`** で書く —— 失敗・取り消しの周期は動かさない。ここが「失敗した周期を成功と
//   記録する」形になると、次の判定が「まだ期限内」と読み、検出が 1 日ずつ取りこぼされる。まだ 1 度も成功していなければ null。
// - `LastAttemptedAt` / `AttemptsSinceSuccess`: 周期を**始める前に**別の保存で書く試行の記録（[[IADR-0496]] 決定 4）。
//   試行が成功すると 0 に戻る。0 でないまま次の判定に来たら、前の試行は成功しなかった（例外・取り消し・**プロセスごと
//   落ちた**）ということであり、指数バックオフで次の試行を遅らせる。プロセス内の数だけだと、メモリ不足で落ちる周期が
//   「再起動 → 起動の待ち → 再実行 → 落ちる」を繰り返す（再起動で数が消える）。
//
// クラスタの `DetectedAt` からは導けない（構成が変わらない再検出は時刻を進めない ——
// [[IADR-0425]] の不変条件。文書 0 件ならクラスタの行そのものが無い）。だから別の行に持つ。
public class GraphBatchRun
{
    public const int MaxJobNameLength = 64;

    public string JobName { get; private set; } = string.Empty;

    public DateTimeOffset? LastSucceededAt { get; private set; }

    public DateTimeOffset LastAttemptedAt { get; private set; }

    public int AttemptsSinceSuccess { get; private set; }

    // 成功した周期の記録（試験の種まきと、記録が無いまま成功したとき）。
    public static GraphBatchRun Succeeded(string jobName, DateTimeOffset startedAt) => new()
    {
        JobName = jobName,
        LastSucceededAt = startedAt,
        LastAttemptedAt = startedAt,
        AttemptsSinceSuccess = 0,
    };

    // 初めての試行の記録（まだ成功していない）。
    public static GraphBatchRun Attempted(string jobName, DateTimeOffset attemptedAt) => new()
    {
        JobName = jobName,
        LastSucceededAt = null,
        LastAttemptedAt = attemptedAt,
        AttemptsSinceSuccess = 1,
    };

    // 周期を始める前に呼ぶ（別の保存で確定させる）。
    public void MarkAttempted(DateTimeOffset attemptedAt)
    {
        LastAttemptedAt = attemptedAt;
        AttemptsSinceSuccess++;
    }

    // 周期が成功したときだけ呼ぶ（本体の書き込みと同じ保存で確定させる）。
    public void MarkSucceeded(DateTimeOffset startedAt)
    {
        LastSucceededAt = startedAt;
        if (LastAttemptedAt < startedAt)
            LastAttemptedAt = startedAt;
        AttemptsSinceSuccess = 0;
    }
}
