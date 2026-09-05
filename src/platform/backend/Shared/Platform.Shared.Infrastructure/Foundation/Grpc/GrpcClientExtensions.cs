using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Platform.Shared.Infrastructure.Foundation.Grpc;

// NFR-09, NFR-16, ADR-0029, IADR-0379 決定 3・4 (#1201): east-west gRPC の呼び出し側の共通部品。
//
// - `AddPlatformServiceToken`: s2s トークンの発行側（client credentials）を DI へ登録する。
// - `CreatePlatformChannel`: h2c（`http://`）のチャネルに s2s トークンの CallCredentials を付ける。
//   🔴 平文チャネルに CallCredentials を付けるには `UnsafeUseInsecureChannelCallCredentials` が要る
//   （既定では TLS 無しのチャネルでトークンを送らない）。メッシュ内の TLS はサイドカーが終端するので、
//   ここで許すのはアプリから見た平文であり、線上は mTLS である。
//
// キャッシュ・タイムアウト・リトライ・fail-safe は呼び出し元サービスの Infrastructure に置く
// （ADR-0029 2026-08-04 追記）。ここには資格情報の付け方だけを置く。
public static class GrpcClientExtensions
{
    public static IServiceCollection AddPlatformServiceToken(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ServiceTokenOptions>(config.GetSection(ServiceTokenOptions.SectionName));
        services.AddHttpClient(ClientCredentialsServiceTokenProvider.HttpClientName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IServiceTokenProvider, ClientCredentialsServiceTokenProvider>();
        return services;
    }

    public static CallCredentials CreateServiceCallCredentials(IServiceTokenProvider tokenProvider) =>
        CallCredentials.FromInterceptor(async (context, metadata) =>
        {
            var token = await tokenProvider.GetTokenAsync(context.CancellationToken);
            metadata.Add("Authorization", $"Bearer {token}");
        });

    public static GrpcChannel CreatePlatformChannel(string address, IServiceTokenProvider tokenProvider) =>
        GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            Credentials = ChannelCredentials.Create(
                ChannelCredentials.Insecure, CreateServiceCallCredentials(tokenProvider)),
            UnsafeUseInsecureChannelCallCredentials = true,
        });
}
