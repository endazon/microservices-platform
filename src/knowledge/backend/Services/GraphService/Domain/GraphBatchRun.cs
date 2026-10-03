namespace GraphService.Domain;

// FR-17, ADR-0035 決定 3, [[IADR-0496]] (#1733): 定期バッチの**最後に成功した周期の開始時刻**（バッチ 1 種につき 1 行）。
//
// 🔴 **成功した周期だけが書く。** 書き手はバッチの本体であり、本体の書き込みと**同じ `SaveChanges`** で書く ——
// 失敗・取り消しの周期は行を動かさない。ここが「失敗した周期を成功と記録する」形になると、
// 次の判定が「まだ期限内」と読み、検出が 1 日ずつ取りこぼされる。
//
// クラスタの `DetectedAt` からは導けない（構成が変わらない再検出は時刻を進めない ——
// [[IADR-0425]] の不変条件。文書 0 件ならクラスタの行そのものが無い）。だから別の行に持つ。
public class GraphBatchRun
{
    public const int MaxJobNameLength = 64;

    public string JobName { get; private set; } = string.Empty;

    public DateTimeOffset LastSucceededAt { get; private set; }

    public static GraphBatchRun Create(string jobName, DateTimeOffset startedAt) => new()
    {
        JobName = jobName,
        LastSucceededAt = startedAt,
    };

    public void MarkSucceeded(DateTimeOffset startedAt) => LastSucceededAt = startedAt;
}
