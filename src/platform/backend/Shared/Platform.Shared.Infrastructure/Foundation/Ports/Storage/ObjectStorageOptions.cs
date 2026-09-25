namespace Platform.Shared.Infrastructure.Foundation.Ports.Storage;

// FR-06, FR-12, ADR-0014/ADR-0015（Superseded by ADR-0106）, IADR-0024: S3 互換オブジェクトストレージ（SeaweedFS）の接続設定。
// 設定セクション `ObjectStorage` からバインドする。Endpoint 未設定の dev/test 環境では
// 実クライアントを構成せず縮退クライアント（NullObjectStorageClient）にフォールバックする。
public sealed class ObjectStorageOptions
{
    public const string SectionName = "ObjectStorage";

    // S3 互換エンドポイント（例: http://seaweedfs:8333）。未設定なら縮退する。
    public string? Endpoint { get; set; }

    // 資格情報。実運用では Secret 経由で注入する（コミットしない）。
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }

    // 正規化本文・資産を格納するバケット名。参照 URI は storage://<Bucket>/<key>。
    public string Bucket { get; set; } = "knowledge-normalized";

    // S3 リージョン（自前ホストの S3 互換ストアでは署名に使うだけ。既定 us-east-1）。
    public string Region { get; set; } = "us-east-1";

    // 自前ホストの S3 互換ストア（SeaweedFS）は仮想ホスト形式ではなくパス形式アクセスを使う。
    public bool ForcePathStyle { get; set; } = true;

    // 起動時にバケットの存在を保証し、バージョニングを有効化する（ADR-0014 版管理）。
    public bool EnsureBucketOnStartup { get; set; } = true;

    // 冪等な再変換（IADR-0008）でも履歴を残すため、バケットのバージョニングを有効化する。
    public bool EnableVersioning { get; set; } = true;

    // 署名付き URL の既定有効期限（分）。ABAC 判定後の一時ダウンロード用（ADR-0014）。
    public int PresignedUrlExpiryMinutes { get; set; } = 15;

    // 実クライアントを構成できるか（Endpoint と資格情報が揃っているか）。
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint)
        && !string.IsNullOrWhiteSpace(AccessKey)
        && !string.IsNullOrWhiteSpace(SecretKey);
}
