using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Platform.Shared.Infrastructure.Foundation.Grpc;

namespace Platform.Shared.Infrastructure.Foundation.Llm;

// FR-02, FR-04, FR-11, NFR-09, ADR-0004, ADR-0010, ADR-0016, ADR-0084 決定 1,
// [[IADR-0379]] 決定 4, [[IADR-0413]], [[IADR-0424]] (#1364):
// LlmGateway の **REST 面**（`/complete`・`/complete/stream`・`/embed`）を叩くクライアントへ
// s2s トークンを付ける。受け口が `ServiceCaller` を要するようになったためである。
//
// 🔴 **付け方の宣言は 1 か所である。** 呼び出し元は 5 つ（AiAnalysis / Conversion / Graph /
// Ingestion / Retrieval）あり、**資格情報の付け方が呼び出し元ごとに散ると 1 つだけが古くなった
// 状態が作れる** —— 本リポジトリが繰り返し踏んでいる形であり、`AuthzScopeHttpClient` が
// 同じ理由で登録を 1 か所に畳んでいる（[[IADR-0413]]）。
//
// 🔴 **アドレスはここで決めない。** `AuthzScopeHttpClient` と違い、既定アドレスは呼び出し元ごとに
// 揃っていない（`GraphService` だけ `5010`・他は `5007`）。**本 PR はその不揃いを直さない** ——
// 直すと「認可を掛ける」変更に「宛先を変える」変更が混ざり、壊れたときにどちらが原因か分からなくなる。
// ここが引き受けるのは**資格情報の付け方だけ**である。
//
// 🔴 **利用者のトークンは載せない**（`ServiceTokenHandler` の責務。載せると confused deputy になる）。
public static class LlmGatewayHttpClient
{
    /// <summary>
    /// LlmGateway の REST 面を叩く名前つき／型つきクライアントへ s2s トークンを付ける。
    /// <para>
    /// トークンが取れないときは <see cref="ServiceTokenHandler"/> が
    /// <see cref="HttpRequestException"/> へ畳む —— 呼び出し元 5 つはいずれも
    /// 「ゲートウェイへ届かない」を既存の縮退（画像保持・提案なし・埋め込みなし・縮退文言）へ
    /// 倒す枝を持っており、**新しい枝を作らずそこへ合流させる**。
    /// </para>
    /// </summary>
    public static IHttpClientBuilder AddLlmGatewayServiceToken(
        this IHttpClientBuilder builder, IConfiguration config)
    {
        // `TryAdd` 主体なので、gRPC 客体やスコープ解決の客体を登録済みのサービスが重ねて呼んでも 1 つのままである。
        builder.Services.AddPlatformServiceToken(config);
        builder.Services.TryAddTransient<ServiceTokenHandler>();
        return builder.AddHttpMessageHandler<ServiceTokenHandler>();
    }
}
