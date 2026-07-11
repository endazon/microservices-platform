using Microsoft.AspNetCore.Http;

namespace Platform.Shared.Infrastructure.Foundation.Middleware;

public class CorrelationIdMiddleware(RequestDelegate next)
{
    private const string Header = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(Header, out var correlationId))
            correlationId = Guid.NewGuid().ToString();

        context.Response.Headers[Header] = correlationId.ToString();
        using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId.ToString()))
        {
            await next(context);
        }
    }
}
