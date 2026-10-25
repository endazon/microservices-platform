using Knowledge.Contracts.Events;

namespace GraphService.Domain;

// 🔴 FR-17, FR-19, IADR-0638 決定 2 (#2150 / #2251): **文書ごとに、グラフへ最後に反映した版**（版の印・墓石）。
//
// ノード（`GraphDocument`）と別の行に持つ —— 撤収（露出 OFF・アーカイブ）と削除でノードの行を消しても印は残し、その後に遅れて届いた
// 古い `DocumentUpdated` でノードを作り直さない（#2251 の載り直し）。古い撤収が新しいノードを消すこともしない。
// 持つのは文書 ID・時刻・連番・削除済みの旗だけで、題名・属性は持たない（ADR-0057 決定 2 が許す「削除の事実の記録」と同じ性質。
// Wiki の印〔IADR-0596〕・取り込みの版の印〔IADR-0595〕と同じ扱い）。保持は無期限（消すと墓石の意味が無くなる）。
public class GraphDocumentSyncMark
{
    public Guid DocumentId { get; private set; }

    // 反映した版の時刻（`DocumentUpdated.UpdatedAt`、削除なら `DocumentDeleted.DeletedAt`）。保存の精度（マイクロ秒）に丸めて持つ。
    public DateTimeOffset AppliedAt { get; private set; }

    // 反映した版の発行の連番（IADR-0637）。旧形式の電文を反映したら null。
    public long? AppliedSequence { get; private set; }

    // 最後に反映したのが削除か。削除と同じ鍵の更新も古いとして扱う（削除は寿命の最後の出来事である）。
    public bool Deleted { get; private set; }

    private GraphDocumentSyncMark() { }

    public static GraphDocumentSyncMark For(Guid documentId) => new() { DocumentId = documentId };

    public void RecordUpdated(DateTimeOffset updatedAt, long? sequence) => Record(updatedAt, sequence, deleted: false);

    public void RecordDeleted(DateTimeOffset deletedAt, long? sequence) => Record(deletedAt, sequence, deleted: true);

    private void Record(DateTimeOffset at, long? sequence, bool deleted)
    {
        AppliedAt = GraphSyncOrdering.ToStoredPrecision(at);
        AppliedSequence = sequence;
        Deleted = deleted;
    }
}

// FR-17, IADR-0638 決定 1 (#2150 / #2251): 到着したイベントをグラフへ反映するかの判定（純関数）。前後は契約の唯一の点で決める。
public static class GraphSyncOrdering
{
    // PostgreSQL の timestamptz はマイクロ秒で保存する。ティックのまま比べると、同じ版の再配送が保存値より「新しい」と読めてしまい、
    // 同じ時刻の逆転（#2150 の根）を見分けられない。
    public static DateTimeOffset ToStoredPrecision(DateTimeOffset at)
        => new(at.UtcTicks - at.UtcTicks % 10, TimeSpan.Zero);

    // 🔴 印より厳密に古い更新は反映しない。削除を反映済みなら、削除と同じ鍵の更新も反映しない。印が無い（初回・移行前）なら反映する。
    public static bool IsStaleUpdate(GraphDocumentSyncMark? mark, DateTimeOffset updatedAt, long? sequence)
    {
        if (mark is null) return false;
        var order = DocumentEventOrder.Compare(ToStoredPrecision(updatedAt), sequence, mark.AppliedAt, mark.AppliedSequence);
        return order < 0 || (mark.Deleted && order == 0);
    }

    // 🔴 印より厳密に古い削除は適用しない（作り直した文書を、遅れて届いた古い削除で消さない）。等しい鍵（削除の再配送）は適用する。
    public static bool IsStaleDelete(GraphDocumentSyncMark? mark, DateTimeOffset deletedAt, long? sequence)
        => mark is not null
           && DocumentEventOrder.IsOlder(ToStoredPrecision(deletedAt), sequence, mark.AppliedAt, mark.AppliedSequence);
}
