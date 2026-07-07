namespace ConversionService.Worker.Foundation.Ports;

// FR-12, ADR-0014: 正規化本文（Markdown）・資産（画像）を S3 互換オブジェクトストレージへ
// 保管するポート。文書管理サービスはメタデータ＋参照（オブジェクトキー/URI）を持つ。
public interface IObjectStore
{
    // 正規化 Markdown 本文を保管し、参照 URI を返す。
    Task<string> SaveMarkdownAsync(string key, string markdown, CancellationToken ct = default);

    // 画像資産を保管し、参照 URI を返す。
    Task<string> SaveAssetAsync(string key, byte[] bytes, string contentType,
        CancellationToken ct = default);
}
