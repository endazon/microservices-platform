namespace IngestionService.Worker.Domain.Ports;

// FR-02, UC-04: 文書本文（Markdown）取得ポート（パース段）
// MarkdownUri が指す本文を読み込み、チャンク化に渡せる文字列を返す。
public interface IDocumentContentReader
{
    Task<string> ReadAsync(string markdownUri, string title, CancellationToken ct = default);
}
