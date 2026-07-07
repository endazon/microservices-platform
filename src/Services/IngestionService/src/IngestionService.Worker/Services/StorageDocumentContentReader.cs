using KnowledgePlatform.Shared.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace IngestionService.Worker.Services;

// FR-02/FR-06, UC-04: 文書本文の取得（パース段）。
// storage:// URI は S3 互換オブジェクトストレージ（MinIO, ADR-0015）から実本文を取得する
// （ABAC を強制する取り込みサービス経由のサーバサイド読み取り。IADR-0017 と整合）。
// http(s) URI は HTTP で取得する。いずれでもない／ストレージ未配備のときはプレースホルダー本文へ縮退する。
public class StorageDocumentContentReader(
    HttpClient http,
    IObjectStorageClient storage,
    ILogger<StorageDocumentContentReader> logger) : IDocumentContentReader
{
    public async Task<string> ReadAsync(string markdownUri, string title, CancellationToken ct = default)
    {
        // FR-06: オブジェクトストレージ（storage://）から実本文を取得する。
        if (storage.CanResolve(markdownUri))
        {
            var stored = await storage.GetTextAsync(markdownUri, ct);
            logger.LogInformation("Fetched markdown body from object storage {Uri} ({Length} chars)",
                markdownUri, stored.Length);
            return stored;
        }

        if (Uri.TryCreate(markdownUri, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            // 実本文を取得（E2: 失敗時は例外を送出し MassTransit のリトライへ委ねる）
            var content = await http.GetStringAsync(uri, ct);
            logger.LogInformation("Fetched markdown body from {Uri} ({Length} chars)",
                markdownUri, content.Length);
            return content;
        }

        // ストレージ未配備（storage:// を解決できない）等はプレースホルダーへ縮退する。
        logger.LogWarning(
            "Markdown storage not available for {Uri}; using placeholder body", markdownUri);
        return $"# {title}\n\nコンテンツは {markdownUri} から取得します。";
    }
}
