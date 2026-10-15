using Amazon.S3;
using GraphService.Domain.Ports;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace GraphService.Infrastructure.ExternalServices;

// FR-17, FR-06, ADR-0015（Superseded by ADR-0106）, ADR-0033 決定 6, IADR-0281 (#912): 正規化 Markdown 本文の取得。
//
// WikiService の `StorageMarkdownReader` と同型である（storage:// は S3 互換オブジェクトストレージ
// （SeaweedFS）から、http(s) は HTTP で取得する）。**唯一の違いは縮退の向きである** ——
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
            string stored;
            try
            {
                stored = await storage.GetTextAsync(markdownUri!, ct);
            }
            catch (AmazonS3Exception ex) when (ObjectStorageErrors.IsNoSuchKey(ex))
            {
                // 🔴 FR-17, [[IADR-0549]] 決定 4 (#1931 項目 3)・[[IADR-0540]] 決定 2 と同じ判定: **キーが無いのは恒久的な欠落である。**
                // 例外を上げると再試行の後 DLQ へ入る（IADR-0540・IADR-0541 の残余だった）。契約どおり null を返して抽出をスキップする
                // （辺は触らない・語の出現数は表題だけ）。本文の無い古い文書のアーカイブ（`DocumentUpdated` の fan-out）がここへ届き得る。
                // 一時障害（時間切れ・5xx・接続の失敗・バケットが無い）はここへ来ない —— 例外のまま上げて再試行へ委ねる。
                logger.LogWarning(
                    "Markdown body object missing (NoSuchKey) at {Uri}; link extraction is skipped", markdownUri);
                return null;
            }
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
