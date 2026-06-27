using Microsoft.Extensions.Logging;

namespace IngestionService.Worker.Services;

// FR-02, UC-04: 文書本文の取得（パース段）。
// http(s) URI は HTTP で取得する。それ以外（storage:// 等、dev 環境に未配備の
// オブジェクトストレージ）はプレースホルダー本文へグレースフルデグレードする
// （ConversionService の PandocConversionService と同じ方針）。
public class StorageDocumentContentReader(
    HttpClient http,
    ILogger<StorageDocumentContentReader> logger) : IDocumentContentReader
{
    public async Task<string> ReadAsync(string markdownUri, string title, CancellationToken ct = default)
    {
        if (Uri.TryCreate(markdownUri, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            // 実本文を取得（E2: 失敗時は例外を送出し MassTransit のリトライへ委ねる）
            var content = await http.GetStringAsync(uri, ct);
            logger.LogInformation("Fetched markdown body from {Uri} ({Length} chars)",
                markdownUri, content.Length);
            return content;
        }

        // dev 環境ではオブジェクトストレージが未配備のためプレースホルダーを返す。
        logger.LogWarning(
            "Markdown storage not available for {Uri}; using placeholder body", markdownUri);
        return $"# {title}\n\nコンテンツは {markdownUri} から取得します。";
    }
}
