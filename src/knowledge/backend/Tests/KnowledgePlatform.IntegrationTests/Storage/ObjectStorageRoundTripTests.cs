using KnowledgePlatform.Shared.Infrastructure.Composable.Adapters.Storage;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using FluentAssertions;
using KnowledgePlatform.IntegrationTests.Fixtures;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Ports.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.Minio;

namespace KnowledgePlatform.IntegrationTests.Storage;

// FR-06, FR-12, UC-03/UC-06, ADR-0014/ADR-0015, IADR-0024: MinIO 実体への保存→取得ラウンドトリップ、
// 冪等な再変換（同一キー上書き）、バージョニング有効化を検証する（受け入れ基準: 実本文の永続化）。
public sealed class ObjectStorageRoundTripTests
{
    private const string AccessKey = "minioadmin";
    private const string SecretKey = "minioadmin";

    private static async Task<(IAmazonS3 S3, ObjectStorageOptions Options)> ConnectAsync(MinioContainer minio)
    {
        var options = new ObjectStorageOptions
        {
            Endpoint = minio.GetConnectionString(),
            AccessKey = AccessKey,
            SecretKey = SecretKey,
            Bucket = "test-normalized",
            ForcePathStyle = true,
            EnableVersioning = true
        };
        var s3 = new AmazonS3Client(
            new BasicAWSCredentials(AccessKey, SecretKey),
            new AmazonS3Config
            {
                ServiceURL = options.Endpoint,
                ForcePathStyle = true,
                AuthenticationRegion = options.Region
            });
        var client = new S3ObjectStorageClient(s3, options, NullLogger<S3ObjectStorageClient>.Instance);
        await client.EnsureBucketAsync();
        return (s3, options);
    }

    // 本文（Markdown）・資産（バイナリ）を保存し、参照 URI から実体を取得できる（プレースホルダーの解消）。
    [DockerFact]
    public async Task Persists_and_reads_markdown_and_asset()
    {
        var minio = new MinioBuilder().WithImage("minio/minio:RELEASE.2025-04-08T15-41-24Z")
            .WithUsername(AccessKey).WithPassword(SecretKey).Build();
        await minio.StartAsync();
        try
        {
            var (s3, options) = await ConnectAsync(minio);
            var client = new S3ObjectStorageClient(s3, options, NullLogger<S3ObjectStorageClient>.Instance);

            var mdUri = await client.PutTextAsync("doc-1/document.md", "# 本文\nhello", "text/markdown");
            var assetBytes = Encoding.UTF8.GetBytes("PNGDATA");
            var assetUri = await client.PutBytesAsync("doc-1/assets/fig-1.png", assetBytes, "image/png");

            mdUri.Should().Be("storage://test-normalized/doc-1/document.md");
            client.CanResolve(mdUri).Should().BeTrue();

            (await client.GetTextAsync(mdUri)).Should().Be("# 本文\nhello");
            (await client.GetBytesAsync(assetUri)).Should().Equal(assetBytes);
        }
        finally
        {
            await minio.DisposeAsync();
        }
    }

    // 冪等な再変換（同一キー）は上書きされ、最新本文が読める。バージョニングで履歴は保持される。
    [DockerFact]
    public async Task Reconversion_overwrites_same_key_idempotently()
    {
        var minio = new MinioBuilder().WithImage("minio/minio:RELEASE.2025-04-08T15-41-24Z")
            .WithUsername(AccessKey).WithPassword(SecretKey).Build();
        await minio.StartAsync();
        try
        {
            var (s3, options) = await ConnectAsync(minio);
            var client = new S3ObjectStorageClient(s3, options, NullLogger<S3ObjectStorageClient>.Instance);

            var first = await client.PutTextAsync("doc-2/document.md", "v1", "text/markdown");
            var second = await client.PutTextAsync("doc-2/document.md", "v2", "text/markdown");

            second.Should().Be(first); // 決定的キー（IADR-0008）＝同一参照 URI
            (await client.GetTextAsync(second)).Should().Be("v2");

            // 署名付き URL（ABAC 判定後の一時 DL 用）が発行できる。
            client.CreatePresignedGetUrl(second).Should().StartWith("http");
        }
        finally
        {
            await minio.DisposeAsync();
        }
    }
}
