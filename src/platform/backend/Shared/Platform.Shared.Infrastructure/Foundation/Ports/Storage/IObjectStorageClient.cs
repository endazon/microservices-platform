namespace Platform.Shared.Infrastructure.Foundation.Ports.Storage;

// FR-06, FR-12, ADR-0014/ADR-0015（Superseded by ADR-0106）: S3 互換オブジェクトストレージ（SeaweedFS）への保存・取得ポート。
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

    // FR-06, NFR-16, [[IADR-0544]] 決定 4（2026-10-17 追記 / #1940）: storage:// URI からテキスト本文を**上限つきで**取得する。
    //
    // 🔴 **上限（`maxBytes`。格納されたバイト数）を超える本文は全文を読まない。** 実クライアントは応答の大きさ（ContentLength）を
    // 先に見て、超えれば本文を 1 バイトも読まずに `Exceeded` を返す。大きさが分からない・偽っている場合も、読むのは上限＋1 バイトまでで打ち切る
    // （このとき `SizeBytes` は下限＝上限＋1）。切り詰めた本文は返さない。
    // NoSuchKey・構成の不調などの失敗は `GetTextAsync` と同じく例外のまま上げる。
    //
    // 既定の実装は「対応していない」を投げる（`ExistsAsync` と同じ理由。実クライアントと縮退クライアントは自分で実装する）。
    Task<BoundedTextRead> GetTextBoundedAsync(string uri, long maxBytes, CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"{GetType().Name} は上限つきの本文の取得（GetTextBoundedAsync）に対応していません: {uri}");

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

    // FR-06, [[IADR-0549]] (#1931 項目 3): storage:// URI が指すオブジェクトが**在るか**を、本文を読み出さずに確かめる。
    //
    // 🔴 **「無い」と答えるのはキーが無い（NoSuchKey。`ObjectStorageErrors.IsNoSuchKey`）ときだけである。**
    // バケットが無い・権限が無い・時間切れ・5xx は例外のまま上げる（「無い」に倒さない —— 呼び出し側が
    // 「無い」を根拠に状態を変える〔アーカイブする〕ので、構成の不調を欠落と取り違えると在る文書まで動かす）。
    //
    // 既定の実装は「対応していない」を投げる（試験の偽物が全部これを実装しなくて済むようにするため。
    // 実クライアント〔`S3ObjectStorageClient`〕と縮退クライアント〔`NullObjectStorageClient`〕は自分で実装する）。
    Task<bool> ExistsAsync(string uri, CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"{GetType().Name} はオブジェクトの存在確認（ExistsAsync）に対応していません: {uri}");

    // 与えられた URI をこのクライアントで解決（取得）できるか。
    // storage:// スキームかつ実クライアントが構成済みのとき true。
    bool CanResolve(string? uri);

    // ABAC 判定後の一時ダウンロード用に、署名付き GET URL を発行する（ADR-0014）。
    // 直接公開はせず、サービスが認可した呼び出し元にのみ払い出す。
    string CreatePresignedGetUrl(string uri, TimeSpan? expiry = null);
}
