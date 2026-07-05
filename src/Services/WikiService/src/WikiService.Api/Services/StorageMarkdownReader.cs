namespace WikiService.Api.Services;

// FR-13, UC-07, IADR-0021: 正規化 Markdown 本文の取得。
// http(s) URI は HTTP で実取得し、それ以外（storage:// 等、dev 環境に未配備のオブジェクトストレージ）や
// URI 未指定はプレースホルダー本文へグレースフルデグレードする
// （IngestionService.StorageDocumentContentReader と同一方針）。
public class StorageMarkdownReader(HttpClient http, ILogger<StorageMarkdownReader> logger) : IWikiContentReader
{
    public async Task<string> ReadAsync(string? markdownUri, string title, CancellationToken ct = default)
    {
        if (markdownUri is not null
            && Uri.TryCreate(markdownUri, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            // 実本文を取得（失敗時は例外を送出し MassTransit のリトライへ委ねる）。
            var content = await http.GetStringAsync(uri, ct);
            logger.LogInformation("Fetched normalized markdown from {Uri} ({Length} chars)",
                markdownUri, content.Length);
            return content;
        }

        // dev 環境ではオブジェクトストレージが未配備のためプレースホルダーを返す。
        logger.LogWarning(
            "Markdown storage not available for {Uri}; using placeholder body", markdownUri ?? "(null)");
        return $"# {title}\n\nコンテンツは {markdownUri ?? "(未設定)"} から取得します。";
    }
}
