namespace WikiService.Domain;

// FR-13, FR-06, ADR-0027, [[IADR-0596]] 決定 1 (#2102): **文書ごとに、Wiki へ最後に反映した版**（版の印）。
//
// 同期メタデータ（`WikiPage`）と別の行に持つ —— 撤去（露出の門）・削除で `Pages` の行を消しても印は残し、
// その後に遅れて届いた古い `DocumentUpdated` で Wiki.js のページとメタデータを書き戻さないためである
// （IngestionService の版の印〔[[IADR-0595]] 決定 1〕と同じ考え方）。持つのは文書 ID と時刻だけで、題名・本文・属性の値は持たない
// （ADR-0057 決定 2 が許す「削除の事実の記録」と同じ性質）。
//
// `Revision` は保存の突き合わせ（並行トークン）である。2 つのレプリカ・更新と削除の 2 本のリスナーが同じ文書を並行に処理すると、
// 後から保存する側が突き合わせで負ける（[[IADR-0596]] 決定 3）。
public class DocumentSyncMark
{
    public Guid DocumentId { get; private set; }

    // 反映した版（`DocumentUpdated.UpdatedAt`、削除なら `DocumentDeleted.DeletedAt`）。保存の精度（マイクロ秒）に丸めて持つ。
    public DateTimeOffset AppliedAt { get; private set; }

    // 最後に反映したのが削除か。削除と同じ時刻の更新も古いとして扱う（削除は同じ文書の最後の出来事である）。
    public bool Deleted { get; private set; }

    public long Revision { get; private set; }

    private DocumentSyncMark() { }

    public static DocumentSyncMark For(Guid documentId) => new() { DocumentId = documentId };

    public void RecordUpdated(DateTimeOffset updatedAt) => Record(updatedAt, deleted: false);

    public void RecordDeleted(DateTimeOffset deletedAt) => Record(deletedAt, deleted: true);

    private void Record(DateTimeOffset at, bool deleted)
    {
        AppliedAt = DocumentSyncOrdering.ToStoredPrecision(at);
        Deleted = deleted;
        Revision++;
    }
}

// FR-13, [[IADR-0596]] 決定 1・2 (#2102): 到着したイベントを Wiki へ反映するかの判定（純関数）。
public static class DocumentSyncOrdering
{
    // Postgres の timestamptz はマイクロ秒で保存する。ティック（100 ナノ秒）のまま比べると、同じ版の再配送が
    // 保存値より「新しい」と読めてしまい、等しいことを判定できない（[[IADR-0595]] がミリ秒で比べるのと同じ理由）。
    public static DateTimeOffset ToStoredPrecision(DateTimeOffset at)
        => new(at.UtcTicks - at.UtcTicks % 10, TimeSpan.Zero);

    // 🔴 **反映済みの版より厳密に古い更新は反映しない**（ack して捨てる）。等しい版は反映する（再配送・再発行は同じ版を運ぶ）。
    // 削除を反映済みなら、削除と同じ時刻の更新も反映しない。印が無い（初回・移行前）なら反映する。
    public static bool IsStaleUpdate(DocumentSyncMark? mark, DateTimeOffset updatedAt)
    {
        if (mark is null) return false;
        var incoming = ToStoredPrecision(updatedAt);
        return incoming < mark.AppliedAt || (mark.Deleted && incoming <= mark.AppliedAt);
    }

    // 🔴 **反映済みの版より厳密に古い削除は適用しない。** 文書 ID は変換が決定的に採番するので、削除の後に同じ ID で作り直された
    // 文書がある。その版を反映した後に遅れて届いた古い削除で、作り直したページを消さない。等しい時刻（削除の再配送）は適用する。
    public static bool IsStaleDelete(DocumentSyncMark? mark, DateTimeOffset deletedAt)
        => mark is not null && ToStoredPrecision(deletedAt) < mark.AppliedAt;
}
