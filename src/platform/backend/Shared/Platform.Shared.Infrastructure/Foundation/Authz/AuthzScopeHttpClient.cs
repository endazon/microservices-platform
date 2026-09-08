using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Shared.Infrastructure.Foundation.Grpc;

namespace Platform.Shared.Infrastructure.Foundation.Authz;

// FR-05, NFR-09, 計画 ADR-0004, ADR-0029, ADR-0075, **ADR-0088 決定 2**,
// [[IADR-0379]] 決定 4・5, [[IADR-0412]], [[IADR-0413]] (#1333):
// ABAC スコープ解決（REST `POST /authz/scope`）専用の HTTP クライアント。
//
// 🔴 **専用にする理由は 2 つあり、どちらも「共有すると壊れる」である。**
//
// 1. **資格情報の意味論が逆な用途と共有されている。** 名前つきクライアント `"AuthorizationService"` は
//    スコープ解決（**何も付けない**）と利用者名簿 `/authz/users`（**利用者の `Authorization` を転送**）の
//    両方に使われている。ここへ s2s トークンを付けると、名簿の経路で**利用者のトークンと s2s が混ざる**。
//    🔴 **これは PR #1332（[[IADR-0412]]）が解いたのと同型で、2 回目である**（[[IADR-0141]] の条件）。
// 2. **`ADR-0088` 決定 2 が REST 面の認可を要求した。** 受け口が `ServiceCaller` を要るようになったので、
//    スコープ解決の経路は s2s トークンを**必ず**付けなければならない。
//
// 🔴 **登録は 1 か所である。** 4 つの呼び出し元（BFF / AiAnalysis / Graph / Wiki）が
// 同じ 1 つの拡張を呼ぶ —— アドレスの既定値や資格情報の付け方が呼び出し元ごとに散ると、
// **1 つだけが古くなった状態が作れる**（本リポジトリが繰り返し踏んでいる形）。
public static class AuthzScopeHttpClient
{
    /// <summary>
    /// スコープ解決専用の名前つきクライアント名。
    /// 🔴 **`"AuthorizationService"` と別名である**（上の理由 1）。
    /// </summary>
    public const string ClientName = "AuthorizationServiceScope";

    /// <summary>接続先。**名簿の経路と同じ構成キー**（宛先は同じサービスである）。</summary>
    public const string AddressKey = "Services:AuthorizationService";

    /// <summary>既定値は移行前の 4 呼び出し元と**同一**である（挙動を変えない）。</summary>
    public const string DefaultAddress = "http://authorization-service:5005";

    public static IServiceCollection AddPlatformAuthzScopeHttpClient(
        this IServiceCollection services, IConfiguration config)
    {
        // `TryAdd` 主体なので、gRPC 客体を登録済みのサービスが重ねて呼んでも 1 つのままである。
        services.AddPlatformServiceToken(config);
        services.TryAddTransient<ServiceTokenHandler>();
        services.AddHttpClient(ClientName, c =>
                c.BaseAddress = new Uri(config[AddressKey] ?? DefaultAddress))
            .AddHttpMessageHandler<ServiceTokenHandler>();
        return services;
    }
}

// NFR-09, [[IADR-0379]] 決定 4, [[IADR-0413]] (#1333):
// 発信要求へ**呼び出し側サービス自身**の s2s トークンを付ける。
//
// 🔴 **利用者のトークンは載せない。** 載せると呼び出し先が「利用者が直接呼んだ」と
// 区別できず confused deputy が成立する。ここが載せるのは常にサービス自身の資格である。
public sealed class ServiceTokenHandler(IServiceTokenProvider tokenProvider) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        string token;
        try
        {
            token = await tokenProvider.GetTokenAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // 🔴 **`HttpRequestException` へ畳む。** 呼び出し元 4 つはいずれも
            // `HttpRequestException` / `TaskCanceledException` だけを捕まえて **deny へ縮退**する
            // （`BffScopeResolver` / `RagOrchestrator` / `GraphAccessResolver` / `WikiAccessResolver`）。
            // 素の `InvalidOperationException`（`ServiceToken:ClientId` 未設定など）を通すと、
            // **その 4 つを素通りして呼び出し元の要求が 500 になる** ——
            // 「認可サービスへ届かない」が deny ではなく**障害**に化ける。
            // **新しい枝を作らず、既にある縮退の枝へ合流させる。**
            throw new HttpRequestException(
                "サービス間トークンを取得できませんでした（認可スコープ解決）。", ex);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, ct);
    }
}
