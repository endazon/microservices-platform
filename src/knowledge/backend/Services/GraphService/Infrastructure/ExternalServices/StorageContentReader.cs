using GraphService.Domain.Ports;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace GraphService.Infrastructure.ExternalServices;

// FR-17, FR-06, ADR-0015, ADR-0033 決定 6, IADR-0281 (#912): 正規化 Markdown 本文の取得。
//
// WikiService の `StorageMarkdownReader` と同型である（storage:// は S3 互換オブジェクトストレージ
// （MinIO）から、http(s) は HTTP で取得する）。**唯一の違いは縮退の向きである** ——
// 向こうは表示のためプレースホルダー本文を返すが、こちらは `IGraphContentReader` の契約どおり
// **null を返して抽出をスキップさせる**（プレースホルダーで抽出すると既存の辺が全消しになる）。
public class StorageContentReader(
    HttpClient http,
    IObjectStorageClient storage,
    ILogger<StorageContentReader> logger) : IGraphContentReader
{
    public async Task<string?> ReadAsync(string? markdownUri, CancellationToken ct = default)
    {
        // FR-06: オブジェクトストレージ（storage://）から実本文を取得する。
        // 実取得の失敗（例外）は送出し、Wolverine のリトライへ委ねる。
        if (storage.CanResolve(markdownUri))
        {
            var stored = await storage.GetTextAsync(markdownUri!, ct);
            logger.LogInformation(
                "Fetched normalized markdown from object storage {Uri} ({Length} chars)",
                markdownUri, stored.Length);
            return stored;
        }

        if (markdownUri is not null
            && Uri.TryCreate(markdownUri, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var content = await http.GetStringAsync(uri, ct);
            logger.LogInformation(
                "Fetched normalized markdown from {Uri} ({Length} chars)", markdownUri, content.Length);
            return content;
        }

        // ストレージ未配備（storage:// を解決できない）／URI 未指定／未知のスキーム。
        // **プレースホルダーへは倒さない**（IGraphContentReader の注記）。
        logger.LogWarning(
            "Markdown body unavailable for {Uri}; link extraction is skipped", markdownUri ?? "(null)");
        return null;
    }
}
