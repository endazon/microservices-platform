namespace IngestionService.Domain.Ports;

// FR-02, UC-04: 文書本文（Markdown）取得ポート（パース段）
// MarkdownUri が指す本文を読み込み、チャンク化に渡せる文字列を返す。
// [[IADR-0540]] (#1931): 本文のオブジェクトが恒久的に無い（S3 の NoSuchKey）ときは `DocumentBodyMissingException` を投げる。
// それ以外の失敗（時間切れ・5xx・接続の失敗）は従来どおりの例外のまま上げる（再試行へ委ねる）。
public interface IDocumentContentReader
{
    Task<string> ReadAsync(string markdownUri, string title, CancellationToken ct = default);
}
