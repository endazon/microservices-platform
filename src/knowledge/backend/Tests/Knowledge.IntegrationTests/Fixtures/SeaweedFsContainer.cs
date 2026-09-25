using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Knowledge.IntegrationTests.Fixtures;

// FR-06, FR-12, ADR-0014, ADR-0106 決定 4・5, [[IADR-0461]] (#1499):
// オブジェクトストレージ（SeaweedFS）の**実イメージ**を Testcontainers で起こす定義。
//
// 🔴 **Testcontainers の MinIO モジュール（`MinioBuilder`）は上流で削除された**
// （testcontainers/testcontainers-dotnet#1769。理由は「MinIO のイメージが公開されなくなった」）。
// そのため専用モジュールではなく**汎用の `ContainerBuilder`** で組む（ADR-0106 決定 4 の注記）。
//
// 🔴 **起動形は配備（docker-compose / helm）と同じにする。** 受け入れ試験が確かめたいのは
// 「配備する形の SeaweedFS が、実装の使う S3 機能を満たすか」であり、試験だけ別の起動形で
// 通しても配備の保証にならない。引数の意味は IADR-0461 決定 2 が持つ。
public static class SeaweedFsContainer
{
    /// <summary>
    /// 🔴 **digest で固定する**（ADR-0106 決定 5 / ADR-0107 決定 3）。タグだけでは同じタグの中身が
    /// 差し替わっても検知できない。digest は multi-arch の image index のもの
    /// （amd64 / arm64 / arm / 386）。**compose・helm values と同じ参照**である。
    /// </summary>
    public const string Image =
        "docker.io/chrislusf/seaweedfs:4.47@sha256:ce9e796f1fe6f06968f4c04bdaf8f678dad9c8acdfef3d244133d71bfa6bf882";

    /// <summary>S3 ゲートウェイの待受ポート（SeaweedFS の既定）。</summary>
    public const int S3Port = 8333;

    /// <summary>
    /// `weed server` へ渡す引数（イメージの entrypoint が `-dir=/data` を足す）。
    /// </summary>
    /// <remarks>
    /// 🔴 <c>-master.telemetry=false</c> を外さないこと。**SeaweedFS はテレメトリが既定で有効**であり
    /// （`weed/command/server.go` の `master.telemetry` の既定値は true）、外すと試験のたびに
    /// 外部へ送信が起きる（ADR-0106 決定 5 / 08_data-egress-policy）。
    /// </remarks>
    public static readonly string[] ServerArguments =
    [
        "server",
        // 内部の master / volume / filer は loopback だけで待ち受け、S3 だけを外へ開く。
        "-ip=127.0.0.1",
        "-ip.bind=127.0.0.1",
        "-s3",
        "-s3.ip.bind=0.0.0.0",
        "-s3.port=8333",
        // 使わない付属の口（Iceberg REST カタログ・Lance）は閉じる。
        "-s3.port.iceberg=0",
        "-s3.port.lance=0",
        "-master.telemetry=false",
    ];

    /// <summary>
    /// コンテナを組む。資格情報は環境変数 `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` で渡す
    /// （SeaweedFS はこれを静的な管理者 ID として読み、認証を有効にする）。
    /// </summary>
    public static IContainer Build(string accessKey, string secretKey) =>
        new ContainerBuilder(Image)
            .WithCommand(ServerArguments)
            .WithEnvironment("AWS_ACCESS_KEY_ID", accessKey)
            .WithEnvironment("AWS_SECRET_ACCESS_KEY", secretKey)
            .WithPortBinding(S3Port, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(S3Port).ForPath("/healthz")))
            .Build();

    /// <summary>ホスト側から見た S3 の端点（<c>http://host:port</c>）。</summary>
    public static string EndpointOf(IContainer container) =>
        $"http://{container.Hostname}:{container.GetMappedPublicPort(S3Port)}";

    /// <summary>
    /// **書き込みが通るまで待つ。** `/healthz` は S3 ゲートウェイが待ち受けた時点で 200 を返すが、
    /// volume サーバが master へ登録を終えるまでは実体を書けない（1 プロセス内でも順序は保証されない）。
    /// 試験本体の最初の書き込みがこの起動順の揺れで落ちると、S3 機能の欠如と区別できない。
    /// </summary>
    /// <remarks>
    /// 待つのは**試験用の別バケット**への書き込みであり、受け入れ試験の対象（<c>EnsureBucketAsync</c> と
    /// 試験バケットの操作）には触れない。上限を過ぎたら最後の例外をそのまま投げる（黙って続けない）。
    /// </remarks>
    public static async Task WaitUntilWritableAsync(
        string endpoint, string accessKey, string secretKey, CancellationToken ct)
    {
        using var s3 = new AmazonS3Client(
            new BasicAWSCredentials(accessKey, secretKey),
            new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });

        const string probeBucket = "readiness-probe";
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            try
            {
                try
                {
                    await s3.PutBucketAsync(new PutBucketRequest { BucketName = probeBucket }, ct);
                }
                catch (AmazonS3Exception e) when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
                {
                    // 前回の試行で作れている。
                }

                await s3.PutObjectAsync(
                    new PutObjectRequest { BucketName = probeBucket, Key = "probe", ContentBody = "ok" }, ct);
                return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }
}
