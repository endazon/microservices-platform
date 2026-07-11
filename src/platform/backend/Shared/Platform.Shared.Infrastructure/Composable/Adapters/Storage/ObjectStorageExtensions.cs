using Amazon.Runtime;
using Amazon.S3;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Platform.Shared.Infrastructure.Composable.Adapters.Storage;

// FR-06, FR-12, ADR-0014/ADR-0015, IADR-0024: S3 互換オブジェクトストレージ（MinIO）クライアントの登録。
// 設定セクション `ObjectStorage` を読み、Endpoint と資格情報が揃えば実クライアント
// （S3ObjectStorageClient）、揃わなければ縮退クライアント（NullObjectStorageClient）を登録する。
public static class ObjectStorageExtensions
{
    // 書き込み側・読み取り側の双方で使う共有クライアントを登録する。
    public static IServiceCollection AddPlatformObjectStorage(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = new ObjectStorageOptions();
        configuration.GetSection(ObjectStorageOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        if (options.IsConfigured)
        {
            services.AddSingleton<IAmazonS3>(_ =>
            {
                var s3Config = new AmazonS3Config
                {
                    ServiceURL = options.Endpoint,
                    ForcePathStyle = options.ForcePathStyle,
                    AuthenticationRegion = options.Region
                };
                var credentials = new BasicAWSCredentials(options.AccessKey, options.SecretKey);
                return new AmazonS3Client(credentials, s3Config);
            });
            services.AddSingleton<IObjectStorageClient, S3ObjectStorageClient>();
        }
        else
        {
            services.AddSingleton<IObjectStorageClient, NullObjectStorageClient>();
        }

        return services;
    }

    // 書き込み側（ConversionService）でバケットの存在・バージョニングを起動時に保証する。
    public static IServiceCollection AddPlatformObjectStorageBootstrap(
        this IServiceCollection services)
    {
        services.AddHostedService<ObjectStorageBootstrapHostedService>();
        return services;
    }
}
