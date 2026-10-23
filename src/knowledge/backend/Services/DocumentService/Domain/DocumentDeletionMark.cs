namespace DocumentService.Domain;

// FR-01, FR-06, ADR-0057 決定 2, [[IADR-0596]] 決定 7 (#2102): **組織文書を削除した事実**（文書 ID と時刻だけ）。
//
// 変換は文書 ID を決定的に採番する（同じ原本は同じ ID）。削除より前に作られた `DocumentNormalized` が遅れて届いても（再配送・デッドレターからの
// 再投入）、削除した文書を台帳へ作り直さないために残す。削除より後の再正規化（データソースの再同期）は従来どおり作る。
// 題名・本文・属性の値は持たない —— 復元には使えない（ADR-0057 決定 2 が許す「削除の事実の記録」と同じ性質）。
public class DocumentDeletionMark
{
    public Guid DocumentId { get; private set; }
    public DateTimeOffset DeletedAt { get; private set; }

    private DocumentDeletionMark() { }

    public static DocumentDeletionMark For(Guid documentId, DateTimeOffset deletedAt)
        => new() { DocumentId = documentId, DeletedAt = Document.ToStoredPrecision(deletedAt) };

    // 同じ ID の文書を作り直してまた消したとき。
    public void Advance(DateTimeOffset deletedAt)
    {
        var at = Document.ToStoredPrecision(deletedAt);
        if (at > DeletedAt) DeletedAt = at;
    }

    // 🔴 削除より前に作られた再正規化か（削除と同じ時刻も前とみなす。削除は同じ文書の最後の出来事である）。前なら台帳へ作り直さない。
    public bool Supersedes(DateTimeOffset normalizedAt) => Document.ToStoredPrecision(normalizedAt) <= DeletedAt;
}
