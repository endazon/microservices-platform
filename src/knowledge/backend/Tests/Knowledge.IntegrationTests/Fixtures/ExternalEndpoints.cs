namespace Knowledge.IntegrationTests.Fixtures;

// NFR, [[IADR-0414]] (#1336): コンテナを自前で立てる試験のための**外部供給の口**。
//
// `PostgresFixture`（#1073）と `RabbitMqFixture`（#455 W3）は既にこの口を持っていたが、
// **Qdrant と MinIO は持っていなかった**。そのため Docker Engine API を持たない環境では、
// この 2 つを使う 6 件が**外から与えても走らせようが無かった**。
//
// 🔴 **読み方の規則は 1 か所に置く**（空文字は「未設定」）—— 判定が写ると、
// 片方だけ空文字を通す状態が作れる。
// **4 つの供給口すべてがここを通る**（Postgres / ブローカ / Qdrant / MinIO）——
// 新設の 2 口だけを通すと、「1 か所」と書いてあるのに実装は 3 か所、という状態になる。

/// <summary>Qdrant の外部端点（gRPC の <c>host:port</c>）。</summary>
public static class QdrantEndpoint
{
    public const string ExternalEndpointVariable = "PLATFORM_TEST_QDRANT";

    public static string? External => ExternalEndpoints.Read(ExternalEndpointVariable);
}

/// <summary>
/// MinIO（S3 互換）の外部端点（<c>http://host:port</c>）。
///
/// 🔴 **資格情報は変数にしない。** 試験が使うのは開発用の既定値（`minioadmin`）1 組だけであり、
/// 変数を増やすと「端点だけ変えて資格情報を変え忘れた」状態が作れる。
/// 外部の MinIO を与えるときは**この資格情報で作る**こと（手順は how-to を参照）。
/// </summary>
public static class MinioEndpoint
{
    public const string ExternalEndpointVariable = "PLATFORM_TEST_MINIO";

    /// <summary>試験が前提とする開発用の資格情報。外部供給でも同じ値を使う。</summary>
    public const string AccessKey = "minioadmin";
    public const string SecretKey = "minioadmin";

    public static string? External => ExternalEndpoints.Read(ExternalEndpointVariable);
}

public static class ExternalEndpoints
{
    /// <summary>空文字は「未設定」として扱う（4 つの口で同じ規則を使う）。</summary>
    public static string? Read(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : null;
}
