using Microsoft.AspNetCore.Builder;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Middleware;

namespace KnowledgePlatform.Shared.Infrastructure.Foundation.Extensions;

public static class CommonServiceExtensions
{
    public static WebApplication UseKnowledgePlatformMiddleware(this WebApplication app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }
}
