namespace WikiService.Domain.Ports;

// FR-13, UC-07, IADR-0021: DocumentUpdated の MarkdownUri が指す正規化 Markdown 本文を取得するポート。
// 取得した本文を Wiki.js へ push する（同期）。
public interface IWikiContentReader
{
    Task<string> ReadAsync(string? markdownUri, string title, CancellationToken ct = default);
}
