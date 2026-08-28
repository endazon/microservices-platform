namespace Platform.Shared.Infrastructure.Foundation.Ports.Storage;

// FR-06, FR-12, ADR-0014/ADR-0015: S3 互換オブジェクトストレージ（MinIO）への保存・取得ポート。
// 書き込み側（ConversionService）と読み取り側（IngestionService/WikiService）が共有する。
// 参照 URI は storage://<bucket>/<key>（StorageUri）で表し、実体は既定バケットへ格納する。
public interface IObjectStorageClient
{
    // テキスト（正規化 Markdown 本文）を保存し、参照 URI を返す。
    Task<string> PutTextAsync(string key, string text, string contentType,
        CancellationToken ct = default);

    // バイナリ資産（画像・図）を保存し、参照 URI を返す。
    Task<string> PutBytesAsync(string key, byte[] bytes, string contentType,
        CancellationToken ct = default);

    // storage:// URI からテキスト本文を取得する。
    Task<string> GetTextAsync(string uri, CancellationToken ct = default);

    // storage:// URI からバイナリを取得する。
    Task<byte[]> GetBytesAsync(string uri, CancellationToken ct = default);

    // FR-06, FR-19, ADR-0057 決定 1, IADR-0296: storage:// URI が指すオブジェクトを削除する。
    //
    // 🔴 **全バージョンを消す契約である。** バケットのバージョニングは既定で有効
    // （`ObjectStorageOptions.EnableVersioning` が既定 true。起動時に `VersionStatus.Enabled` を掛ける）
    // であり、**素の DeleteObject は delete marker を書くだけで過去の全版が残る**。
    // ADR-0057 の受け入れ基準は「当該文書の本文・資産が**残っていない**」ことなので、
    // 「最新版が引けなくなる」では満たせない。
    //
    // **冪等である** —— 実在しないキーの削除は成功として扱う（再試行を安全にするため）。
    // 失敗は例外で伝える。呼び出し側は**台帳（DB 行）を消す前に**本メソッドを完了させること
    // （順序の根拠は IADR-0296 決定 3。台帳を先に消すと、実体だけが不可視のまま残る）。
    Task DeleteAsync(string uri, CancellationToken ct = default);

    // 与えられた URI をこのクライアントで解決（取得）できるか。
    // storage:// スキームかつ実クライアントが構成済みのとき true。
    bool CanResolve(string? uri);

    // ABAC 判定後の一時ダウンロード用に、署名付き GET URL を発行する（ADR-0014）。
    // 直接公開はせず、サービスが認可した呼び出し元にのみ払い出す。
    string CreatePresignedGetUrl(string uri, TimeSpan? expiry = null);
}
