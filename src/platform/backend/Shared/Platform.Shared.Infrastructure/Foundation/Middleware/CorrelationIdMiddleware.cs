using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Platform.Shared.Infrastructure.Foundation.Middleware;

// ADR-0006, IADR-0216: 相関 ID をログスコープへ載せ、メトリクス・ログ・トレースの突合を成立させる。
// Serilog の LogContext.PushProperty から ILogger.BeginScope へ移した（出口は OTel Logging SDK）。
// スコープが LogRecord の属性になるのは AddPlatformLogging の IncludeScopes = true による。
//
// NFR-19, NFR-28, [[IADR-0649]] (#2129): 値は CorrelationId.Resolve で決める（無い・形が悪いならトレース ID）。
// 決めた値は HttpContext.Items に置き、CorrelationIdForwardingHandler が後段への呼び出しへ引き継ぐ。
public class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var received = context.Request.Headers.TryGetValue(CorrelationId.Header, out var header) ? header.ToString() : null;
        var correlationId = CorrelationId.Resolve(received);

        context.Items[CorrelationId.ItemKey] = correlationId;
        context.Response.Headers[CorrelationId.Header] = correlationId;
        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId
        }))
        {
            await next(context);
        }
    }
}
