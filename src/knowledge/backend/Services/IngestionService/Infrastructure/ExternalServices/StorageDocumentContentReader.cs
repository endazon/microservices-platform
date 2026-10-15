using IngestionService.Domain.Ports;
using IngestionService.Domain;
using Amazon.S3;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
using Microsoft.Extensions.Logging;

namespace IngestionService.Infrastructure.ExternalServices;

// FR-02/FR-06, UC-04: 文書本文の取得（パース段）。
// storage:// URI は S3 互換オブジェクトストレージ（SeaweedFS, ADR-0015（Superseded by ADR-0106））から実本文を取得する
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
            string stored;
            try
            {
                stored = await storage.GetTextAsync(markdownUri, ct);
            }
            catch (AmazonS3Exception ex) when (IsNoSuchKey(ex))
            {
                // 🔴 FR-02, [[IADR-0540]] 決定 1 (#1931): キーが無いのは恒久的な欠落である（再試行しても現れない）。
                // 一時障害（時間切れ・5xx・接続の失敗・バケットが無い）はここへ来ない —— 例外のまま上げて再試行へ委ねる。
                throw new DocumentBodyMissingException(markdownUri, ex);
            }

            logger.LogInformation("Fetched markdown body from object storage {Uri} ({Length} chars)",
                markdownUri, stored.Length);
            return stored;
        }

        if (Uri.TryCreate(markdownUri, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            // 実本文を取得（E2: 失敗時は例外を送出し Wolverine の再試行→デッドレターへ委ねる。#1771）
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

    // FR-02, [[IADR-0540]] 決定 2 (#1931): **キーが無い（NoSuchKey）だけ**を恒久的な欠落とみなす。
    // SDK の専用の型（`NoSuchKeyException`）か、S3 のエラーコード `NoSuchKey` で判定する（S3 互換の実装が
    // 汎用の `AmazonS3Exception` で返しても拾う）。🔴 **状態コード 404 だけでは判定しない** ——
    // バケットが無い（`NoSuchBucket`）も 404 であり、それは構成の不調（自己修復・再試行の対象）である。
    // メッセージ文字列では判定しない。
    // ［2026-10-15 / #1905・[[IADR-0541]]］判定の本体は共有の `ObjectStorageErrors` へ移した（文書サービスの本文の読み取りと同じ判定）。
    internal static bool IsNoSuchKey(AmazonS3Exception ex) => ObjectStorageErrors.IsNoSuchKey(ex);

    internal const string NoSuchKeyErrorCode = ObjectStorageErrors.NoSuchKeyErrorCode;
}
