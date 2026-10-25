namespace WikiService.Domain.Ports;

// FR-13, UC-07, [[IADR-0541]] 決定 4 (#1934)・[[IADR-0540]] 決定 1 と同じ判定: **本文の所在（MarkdownUri）が指すオブジェクトが恒久的に無い**ことを表す。
//
// `IWikiContentReader` の実装が、オブジェクトストレージの「キーが無い」（S3 の NoSuchKey）をこの例外へ写す。
// Wiki 同期の受け口はこれを一時障害として扱わない（再試行も DLQ への送り込みもしない）。
// 🔴 **時間切れ・5xx・接続の失敗・バケットが無いはこれへ写さない**（従来どおり例外のまま上げて再試行へ委ねる）。
public sealed class WikiBodyMissingException(string markdownUri, Exception innerException)
    : Exception($"Document body object does not exist: {markdownUri}", innerException)
{
    public string MarkdownUri { get; } = markdownUri;
}
