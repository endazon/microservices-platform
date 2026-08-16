using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Platform.Shared.Infrastructure.Foundation.Middleware;

// ADR-0006, IADR-0216: 相関 ID をログスコープへ載せ、メトリクス・ログ・トレースの突合を成立させる。
// Serilog の LogContext.PushProperty から ILogger.BeginScope へ移した（出口は OTel Logging SDK）。
// スコープが LogRecord の属性になるのは AddPlatformLogging の IncludeScopes = true による。
public class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    private const string Header = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(Header, out var correlationId))
            correlationId = Guid.NewGuid().ToString();

        context.Response.Headers[Header] = correlationId.ToString();
        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId.ToString()
        }))
        {
            await next(context);
        }
    }
}
