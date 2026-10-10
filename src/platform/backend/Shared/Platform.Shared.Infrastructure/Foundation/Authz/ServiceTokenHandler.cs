using System.Net.Http.Headers;
using Platform.Shared.Infrastructure.Foundation.Grpc;

namespace Platform.Shared.Infrastructure.Foundation.Authz;

// NFR-09, [[IADR-0379]] 決定 4, [[IADR-0413]] (#1333):
// 発信要求へ**呼び出し側サービス自身**の s2s トークンを付ける。
//
// 🔴 **利用者のトークンは載せない。** 載せると呼び出し先が「利用者が直接呼んだ」と
// 区別できず confused deputy が成立する。ここが載せるのは常にサービス自身の資格である。
//
// ［2026-10-10 / #1255・[[IADR-0533]]］認可スコープ解決の REST クライアント（`AuthzScopeHttpClient`）を撤去したので、
// 本ハンドラは独立したファイルへ移した（名前空間は変えていない）。いまの利用者は、gRPC へ移っていない
// LlmGateway の REST 面の呼び出し（`LlmGatewayHttpClient.AddLlmGatewayServiceToken`）だけである。
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
            // 🔴 **`HttpRequestException` へ畳む。** 呼び出し元は `HttpRequestException` / `TaskCanceledException` だけを
            // 捕まえて縮退する。素の `InvalidOperationException`（`ServiceToken:ClientId` 未設定など）を通すと、
            // 縮退の枝を素通りして呼び出し元の要求が 500 になる ——「後段へ届かない」が縮退ではなく**障害**に化ける。
            // **新しい枝を作らず、既にある縮退の枝へ合流させる。**
            throw new HttpRequestException("サービス間トークンを取得できませんでした。", ex);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, ct);
    }
}
