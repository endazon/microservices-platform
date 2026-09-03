using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace ConversionService.Infrastructure.ExternalServices;

// FR-12, ADR-0014, IADR-0320 決定 3 (#1097), IADR-0356 (#1192): 原本 URI をローカルの読み取り可能
// ファイルへ解決する。pandoc（`PandocConversionService`）と PDF のテキスト層抽出器
// （`PdfTextLayerConverter`）の**両方が同じ経路で原本を取り寄せる**ため、ここへ 1 箇所に置く。
//
// 🔴 **オブジェクトストレージの原本を取り寄せる。** 従前は file スキームとローカルパスしか解決できず、
// `DataSourceSyncService` が発行する storage:// の原本は常に解決不能だった —— つまり変換器を入れても
// 縮退したままだった（IADR-0320）。
internal sealed class RawSourceResolver(IObjectStorageClient storage, ILogger logger)
{
    public async Task<ResolvedSource?> ResolveAsync(string storageUri, string contentType,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(storageUri)) return null;

        // オブジェクトストレージ上の原本は一時ファイルへ落として変換器に食わせる。
        if (storage.CanResolve(storageUri))
        {
            var bytes = await storage.GetBytesAsync(storageUri, ct);
            var temp = Path.Combine(Path.GetTempPath(),
                $"conv-src-{Guid.NewGuid():N}{SourceExtension(storageUri, contentType)}");
            await File.WriteAllBytesAsync(temp, bytes, ct);
            logger.LogInformation("Fetched raw source {Uri} ({Bytes} bytes) for conversion",
                storageUri, bytes.Length);
            return new ResolvedSource(temp, IsTemporary: true);
        }

        if (Uri.TryCreate(storageUri, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
                return File.Exists(uri.LocalPath) ? new ResolvedSource(uri.LocalPath, false) : null;
            // storage:// を実クライアントが解決できない（未構成）場合はここへ来る。
            return null;
        }
        // スキーム無し＝ローカルパスとして扱う。
        return File.Exists(storageUri) ? new ResolvedSource(storageUri, false) : null;
    }

    // 一時ファイルの拡張子。変換器は形式を明示するので必須ではないが、
    // 失敗時のログとダンプが読めるように原本の形を残す。
    private static string SourceExtension(string storageUri, string contentType)
    {
        var fromUri = Path.GetExtension(FileName(storageUri));
        if (!string.IsNullOrEmpty(fromUri)) return fromUri;
        return contentType.ToLowerInvariant() switch
        {
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
            "text/html" or "application/xhtml+xml" => ".html",
            "application/pdf" or "application/x-pdf" => ".pdf",
            _ => ".bin"
        };
    }

    // storage://bucket/key/raw.docx のようにスキーム付きでも拡張子を取れるようにする
    // （Path.GetExtension は URI のクエリ・フラグメントを知らないので、パス部だけを見る）。
    internal static string FileName(string uriOrPath) =>
        Uri.TryCreate(uriOrPath, UriKind.Absolute, out var uri) && !uri.IsFile
            ? uri.AbsolutePath
            : uriOrPath;

    // 解決した原本。オブジェクトストレージから取り寄せたものは変換後に消す。
    internal sealed record ResolvedSource(string Path, bool IsTemporary) : IDisposable
    {
        public void Dispose()
        {
            if (!IsTemporary) return;
            try { File.Delete(Path); }
            catch (IOException) { /* 一時ファイルの後始末は best-effort。 */ }
            catch (UnauthorizedAccessException) { /* 同上。 */ }
        }
    }
}
