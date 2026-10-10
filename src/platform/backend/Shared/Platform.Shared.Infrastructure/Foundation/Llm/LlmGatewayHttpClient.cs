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
// ［2026-10-10 / #1255・[[IADR-0533]]］**REST の並走を撤去したので、呼び出し元は 1 つだけになった** ——
// GraphService のクラスタ要約（`LlmGatewayClusterSummaryClient`。既定 off）である。この経路は gRPC へ**移っていない**
// （gRPC 実装が無い）ので退役の対象外であり、REST の面を使い続ける。従前の 5 呼び出し元（AiAnalysis / Conversion /
// Graph の提案 / Ingestion / Retrieval）の REST 実装は撤去した。
//
// 🔴 **利用者のトークンは載せない**（`ServiceTokenHandler` の責務。載せると confused deputy になる）。
public static class LlmGatewayHttpClient
{
    /// <summary>
    /// LlmGateway の REST 面を叩く名前つき／型つきクライアントへ s2s トークンを付ける。
    /// <para>
    /// トークンが取れないときは <see cref="ServiceTokenHandler"/> が
    /// <see cref="HttpRequestException"/> へ畳む —— 呼び出し元は「ゲートウェイへ届かない」を
    /// 既存の縮退（要約を付けない）へ倒す枝を持っており、**新しい枝を作らずそこへ合流させる**。
    /// </para>
    /// </summary>
    public static IHttpClientBuilder AddLlmGatewayServiceToken(
        this IHttpClientBuilder builder, IConfiguration config)
    {
        // `TryAdd` 主体なので、gRPC 客体を登録済みのサービスが重ねて呼んでも 1 つのままである。
        builder.Services.AddPlatformServiceToken(config);
        builder.Services.TryAddTransient<ServiceTokenHandler>();
        return builder.AddHttpMessageHandler<ServiceTokenHandler>();
    }
}
