using Amazon.S3;
using Amazon.S3.Model;
using WikiService.Domain.Ports;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace WikiService.Infrastructure.ExternalServices;

// FR-13/FR-06, UC-07, IADR-0021: 正規化 Markdown 本文の取得。
// storage:// URI は S3 互換オブジェクトストレージ（SeaweedFS, ADR-0015（Superseded by ADR-0106））から実本文を取得する
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
            string stored;
            try
            {
                stored = await storage.GetTextAsync(markdownUri!, ct);
            }
            catch (AmazonS3Exception ex) when (IsNoSuchKey(ex))
            {
                // 🔴 FR-13, [[IADR-0541]] 決定 4 (#1934)・[[IADR-0540]] 決定 1 と同じ判定: キーが無いのは恒久的な欠落である
                // （再試行しても現れない）。一時障害（時間切れ・5xx・接続の失敗・バケットが無い）はここへ来ない —— 例外のまま上げる。
                throw new WikiBodyMissingException(markdownUri!, ex);
            }
            logger.LogInformation("Fetched normalized markdown from object storage {Uri} ({Length} chars)",
                markdownUri, stored.Length);
            return stored;
        }

        if (markdownUri is not null
            && Uri.TryCreate(markdownUri, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            // 実本文を取得（失敗時は例外を送出しブローカ（Wolverine）のリトライへ委ねる）。
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

    // FR-13, [[IADR-0541]] 決定 4 (#1934): **キーが無い（NoSuchKey）だけ**を恒久的な欠落とみなす（[[IADR-0540]] 決定 1 と同じ）。
    // SDK の専用の型（`NoSuchKeyException`）か、S3 のエラーコード `NoSuchKey` で判定する（S3 互換の実装が汎用の型で返しても拾う）。
    // 🔴 **状態コード 404 だけでは判定しない** —— バケットが無い（`NoSuchBucket`）も 404 であり、構成の不調（再試行の対象）である。
    // メッセージ文字列では判定しない。
    // ［2026-10-15 / #1905・[[IADR-0544]]］判定の本体は共有の `ObjectStorageErrors` に置く（取り込み・文書サービスの本文の読み取りと同じ判定。
    // 読み手ごとに写さない）。挙動は変えない。
    internal static bool IsNoSuchKey(AmazonS3Exception ex) => ObjectStorageErrors.IsNoSuchKey(ex);

    internal const string NoSuchKeyErrorCode = ObjectStorageErrors.NoSuchKeyErrorCode;
}
