using KnowledgePlatform.Shared.Infrastructure.Storage;

namespace WikiService.Api.Services;

// FR-13/FR-06, UC-07, IADR-0021: 正規化 Markdown 本文の取得。
// storage:// URI は S3 互換オブジェクトストレージ（MinIO, ADR-0015）から実本文を取得する
// （ABAC を前段で強制する WikiService ゲートウェイ経由のサーバサイド読み取り。IADR-0017/IADR-0020 と整合）。
// http(s) URI は HTTP で実取得し、いずれでもない／未指定／未配備のときはプレースホルダー本文へ縮退する。
public class StorageMarkdownReader(
    HttpClient http,
    IObjectStorageClient storage,
    ILogger<StorageMarkdownReader> logger) : IWikiContentReader
{
    public async Task<string> ReadAsync(string? markdownUri, string title, CancellationToken ct = default)
    {
        // FR-06: オブジェクトストレージ（storage://）から実本文を取得する。
        if (storage.CanResolve(markdownUri))
        {
            var stored = await storage.GetTextAsync(markdownUri!, ct);
            logger.LogInformation("Fetched normalized markdown from object storage {Uri} ({Length} chars)",
                markdownUri, stored.Length);
            return stored;
        }

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

        // ストレージ未配備（storage:// を解決できない）／URI 未指定はプレースホルダーへ縮退する。
        logger.LogWarning(
            "Markdown storage not available for {Uri}; using placeholder body", markdownUri ?? "(null)");
        return $"# {title}\n\nコンテンツは {markdownUri ?? "(未設定)"} から取得します。";
    }
}
