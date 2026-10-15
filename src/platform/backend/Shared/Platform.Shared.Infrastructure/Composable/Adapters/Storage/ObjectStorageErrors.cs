using Amazon.S3;
using Amazon.S3.Model;

namespace Platform.Shared.Infrastructure.Composable.Adapters.Storage;

// FR-02, FR-06, [[IADR-0540]] 決定 2, [[IADR-0543]] (#1931, #1905): S3 の例外の分類（読み手が共有する判定）。
//
// 🔴 **キーが無い（NoSuchKey）だけ**を「オブジェクトが無い」とみなす。SDK の専用の型（`NoSuchKeyException`）か、
// S3 のエラーコード `NoSuchKey` で判定する（S3 互換の実装が汎用の `AmazonS3Exception` で返しても拾う）。
// **状態コード 404 だけでは判定しない** —— バケットが無い（`NoSuchBucket`）も 404 であり、それは構成の不調である。
// メッセージ文字列では判定しない。
//
// 判定は読み手ごとに写さず、ここ 1 か所に置く（取り込み〔IADR-0540〕と文書サービスの本文の読み取り〔IADR-0543〕）。
// 欠落をどう扱うか（ack する・404 にする）は呼び出し元が決める。共有の `S3ObjectStorageClient` の挙動は変えない。
public static class ObjectStorageErrors
{
    public const string NoSuchKeyErrorCode = "NoSuchKey";

    public static bool IsNoSuchKey(AmazonS3Exception ex) =>
        ex is NoSuchKeyException || ex.ErrorCode == NoSuchKeyErrorCode;
}
