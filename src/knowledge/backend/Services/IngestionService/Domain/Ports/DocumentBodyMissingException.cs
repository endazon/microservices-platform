namespace IngestionService.Domain.Ports;

// FR-02, UC-04, [[IADR-0540]] 決定 1 (#1931): **本文の所在（MarkdownUri）が指すオブジェクトが恒久的に無い**ことを表す。
//
// `IDocumentContentReader` の実装が、オブジェクトストレージの「キーが無い」（S3 の NoSuchKey）を
// この例外へ写す。取り込みの受け口はこれを一時障害として扱わない（再試行も DLQ への送り込みもしない）。
// 🔴 **時間切れ・5xx・接続の失敗はこれへ写さない**（従来どおり例外のまま上げて再試行へ委ねる）。
// 区別は SDK の例外型とエラーコードで行い、メッセージ文字列では判定しない（`StorageDocumentContentReader`）。
public sealed class DocumentBodyMissingException(string markdownUri, Exception innerException)
    : Exception($"Document body object does not exist: {markdownUri}", innerException)
{
    public string MarkdownUri { get; } = markdownUri;
}
