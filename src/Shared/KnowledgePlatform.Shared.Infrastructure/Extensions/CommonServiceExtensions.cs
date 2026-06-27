using Microsoft.AspNetCore.Builder;
using KnowledgePlatform.Shared.Infrastructure.Middleware;

namespace KnowledgePlatform.Shared.Infrastructure.Extensions;

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
