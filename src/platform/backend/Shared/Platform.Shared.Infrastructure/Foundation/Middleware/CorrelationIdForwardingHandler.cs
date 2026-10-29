using Microsoft.AspNetCore.Http;

namespace Platform.Shared.Infrastructure.Foundation.Middleware;

// NFR-19, ADR-0006, [[IADR-0649]] (#2129): 受けた要求の相関 ID を後段への呼び出し（IHttpClientFactory のすべてのクライアント。
// gRPC のクライアントも同じ工場から作られる）へ引き継ぐ。従前は転送する仕組みが無く、BFF → 後段の 1 ホップで別の値になった。
// ■ 呼び出し側が自分で付けた値は上書きしない。
// ■ 要求の外（バックグラウンド・メッセージの処理）では付けない。後段は traceparent から同じトレース ID を導く（CorrelationId.Resolve）。
public sealed class CorrelationIdForwardingHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Headers.Contains(CorrelationId.Header)
            && accessor.HttpContext?.Items[CorrelationId.ItemKey] is string correlationId)
        {
            request.Headers.TryAddWithoutValidation(CorrelationId.Header, correlationId);
        }
        return base.SendAsync(request, cancellationToken);
    }
}
