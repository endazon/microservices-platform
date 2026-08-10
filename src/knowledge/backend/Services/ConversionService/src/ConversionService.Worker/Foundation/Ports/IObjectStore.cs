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

    // UC-06, IADR-0154 決定 3: 保管済みの正規化 Markdown を読み戻す（人手補正の図ブロック置換に使う）。
    // 解決できない参照（ストレージ未配備の dev 等）は null を返す。
    Task<string?> TryGetMarkdownAsync(string uri, CancellationToken ct = default);
}
