namespace KnowledgePlatform.Shared.Infrastructure.Foundation.Ports.Storage;

// FR-06, FR-12, ADR-0014: 参照 URI（storage://<bucket>/<key>）とオブジェクト座標の相互変換。
// 「メタデータ＋参照」方式（ADR-0014）の参照側の書式を一箇所に集約する。
public static class StorageUri
{
    public const string Scheme = "storage";

    // storage://<bucket>/<key> を組み立てる。key の先頭スラッシュは正規化する。
    // key はスラッシュ区切りのパス。区切り '/' は保ちつつ各セグメントを %XX エスケープし、
    // TryParse 側の Uri.UnescapeDataString と対称にする（日本語・記号を含むキーでも往復可能）。
    public static string Build(string bucket, string key)
    {
        var escapedKey = string.Join('/',
            key.TrimStart('/').Split('/').Select(Uri.EscapeDataString));
        return $"{Scheme}://{bucket}/{escapedKey}";
    }

    // storage:// URI を (bucket, key) に分解する。書式不正なら false。
    public static bool TryParse(string? uri, out string bucket, out string key)
    {
        bucket = string.Empty;
        key = string.Empty;

        if (string.IsNullOrWhiteSpace(uri)
            || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            || !parsed.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bucket = parsed.Host;
        key = parsed.AbsolutePath.TrimStart('/');

        if (string.IsNullOrEmpty(bucket) || string.IsNullOrEmpty(key))
            return false;

        // %XX エスケープを含むキー（日本語・記号）を復元する。
        key = Uri.UnescapeDataString(key);
        return true;
    }

    // storage:// スキームの URI かどうか。
    public static bool IsStorageUri(string? uri) => TryParse(uri, out _, out _);
}
