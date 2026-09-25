using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Knowledge.IntegrationTests.Storage;

// FR-06, FR-12, UC-03/UC-06, ADR-0014/ADR-0015（Superseded by ADR-0106）, IADR-0024:
// オブジェクトストレージ実体への保存→取得ラウンドトリップ、冪等な再変換（同一キー上書き）、
// バージョニング有効化を検証する（受け入れ基準: 実本文の永続化）。
//
// 🔴 ADR-0106 決定 4, [[IADR-0464]] (#1499): **本クラスの 3 件と、各試験の冒頭で走る
// EnsureBucketAsync（バケットの存在確認・作成・バージョニング有効化）が、製品差し替えの受け入れ試験である。**
// digest で固定した SeaweedFS の実イメージ（SeaweedFsContainer.Image）に対して通ることが条件であり、
// 落ちた場合は ADR-0106 決定 1 が覆り、次点（RustFS）で同じ試験を行う（同 決定 2）。
// **3 件が Skipped のままでは試験は済んでいない**（ci.yml の PR 実行は本クラスを外す。回収先は Integration）。
// 🔴 IADR-0232 決定 3: Trait が無いと integration.yml（日次）の --filter "Category=Integration" に
// 拾われず、日次の走査から静かに落ちる（着手前の実測で見つかった欠落）。ci.yml は --filter を
// 持たないので PR は緑のままであり、成功と見分けが付かない。同プロジェクトの他 11 クラスと同じ形に揃える。
[Trait("Category", "Integration")]
public sealed class ObjectStorageRoundTripTests
{
    // [[IADR-0414]] (#1336): 資格情報は外部供給でも同じ値を使う（共有点が持つ）。
    private const string AccessKey = ObjectStorageEndpoint.AccessKey;
    private const string SecretKey = ObjectStorageEndpoint.SecretKey;

    // [[IADR-0414]] (#1336): 外部のオブジェクトストレージが与えられていればコンテナは起こさない。
    // 🔴 **端点の決め方をここ 1 か所に置く** —— 3 つの試験が同じ判断を写すと、
    // 片方だけ外部を見ない状態が作れる。
    private static async Task<IContainer?> StartUnlessSuppliedAsync()
    {
        if (RequiredServices.ObjectStorage.External is not null) return null;

        // ADR-0106 決定 4, [[IADR-0464]] (#1499): Testcontainers の MinIO モジュールは上流で削除された。
        // 汎用コンテナで SeaweedFS を配備と同じ起動形で起こす（定義は SeaweedFsContainer が持つ）。
        var store = SeaweedFsContainer.Build(AccessKey, SecretKey);
        await store.StartAsync(TestContext.Current.CancellationToken);
        await SeaweedFsContainer.WaitUntilWritableAsync(
            SeaweedFsContainer.EndpointOf(store), AccessKey, SecretKey, TestContext.Current.CancellationToken);
        return store;
    }

    private static async Task<(IAmazonS3 S3, ObjectStorageOptions Options)> ConnectAsync(IContainer? store)
    {
        var options = new ObjectStorageOptions
        {
            Endpoint = RequiredServices.ObjectStorage.External ?? SeaweedFsContainer.EndpointOf(store!),
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
    [Fact]
    public async Task Persists_and_reads_markdown_and_asset()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.ObjectStorage);
        var store = await StartUnlessSuppliedAsync();
        try
        {
            var (s3, options) = await ConnectAsync(store);
            var client = new S3ObjectStorageClient(s3, options, NullLogger<S3ObjectStorageClient>.Instance);

            var mdUri = await client.PutTextAsync("doc-1/document.md", "# 本文\nhello", "text/markdown", TestContext.Current.CancellationToken);
            var assetBytes = Encoding.UTF8.GetBytes("PNGDATA");
            var assetUri = await client.PutBytesAsync("doc-1/assets/fig-1.png", assetBytes, "image/png", TestContext.Current.CancellationToken);

            mdUri.Should().Be("storage://test-normalized/doc-1/document.md");
            client.CanResolve(mdUri).Should().BeTrue();

            (await client.GetTextAsync(mdUri, TestContext.Current.CancellationToken)).Should().Be("# 本文\nhello");
            (await client.GetBytesAsync(assetUri, TestContext.Current.CancellationToken)).Should().Equal(assetBytes);
        }
        finally
        {
            if (store is not null) await store.DisposeAsync();
        }
    }

    // FR-06, FR-19, ADR-0057 決定 1, IADR-0296: **削除は全バージョンへ及ぶ。**
    // 🔴 バージョニング有効のバケットで素の DeleteObject を撃つと delete marker が積まれるだけで、
    // `ListVersions` には過去版が残る。ここでは **3 回上書きしてから削除し、版が 1 つも残らない**
    // ことを実ストア（SeaweedFS）で確かめる（単体側は SDK 呼び出しの形しか見られない）。
    [Fact]
    public async Task Delete_removes_every_version()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.ObjectStorage);
        var store = await StartUnlessSuppliedAsync();
        try
        {
            var (s3, options) = await ConnectAsync(store);
            var client = new S3ObjectStorageClient(s3, options, NullLogger<S3ObjectStorageClient>.Instance);

            const string key = "doc-3/document.md";
            await client.PutTextAsync(key, "v1", "text/markdown", TestContext.Current.CancellationToken);
            await client.PutTextAsync(key, "v2", "text/markdown", TestContext.Current.CancellationToken);
            var uri = await client.PutTextAsync(key, "v3", "text/markdown", TestContext.Current.CancellationToken);

            var before = await s3.ListVersionsAsync(new ListVersionsRequest
            { BucketName = options.Bucket, Prefix = key }, TestContext.Current.CancellationToken);
            before.Versions.Should().HaveCountGreaterThan(1, "前提: バージョニングで履歴が積まれている");

            await client.DeleteAsync(uri, TestContext.Current.CancellationToken);

            var after = await s3.ListVersionsAsync(new ListVersionsRequest
            { BucketName = options.Bucket, Prefix = key }, TestContext.Current.CancellationToken);
            (after.Versions ?? []).Where(v => v.Key == key).Should().BeEmpty(
                "delete marker を含め版が 1 つでも残れば ADR-0057 受け入れ基準①を満たさない");
        }
        finally
        {
            if (store is not null) await store.DisposeAsync();
        }
    }

    // 冪等な再変換（同一キー）は上書きされ、最新本文が読める。バージョニングで履歴は保持される。
    [Fact]
    public async Task Reconversion_overwrites_same_key_idempotently()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.ObjectStorage);
        var store = await StartUnlessSuppliedAsync();
        try
        {
            var (s3, options) = await ConnectAsync(store);
            var client = new S3ObjectStorageClient(s3, options, NullLogger<S3ObjectStorageClient>.Instance);

            var first = await client.PutTextAsync("doc-2/document.md", "v1", "text/markdown", TestContext.Current.CancellationToken);
            var second = await client.PutTextAsync("doc-2/document.md", "v2", "text/markdown", TestContext.Current.CancellationToken);

            second.Should().Be(first); // 決定的キー（IADR-0008）＝同一参照 URI
            (await client.GetTextAsync(second, TestContext.Current.CancellationToken)).Should().Be("v2");

            // 署名付き URL（ABAC 判定後の一時 DL 用）が発行できる。
            client.CreatePresignedGetUrl(second).Should().StartWith("http");
        }
        finally
        {
            if (store is not null) await store.DisposeAsync();
        }
    }
}
