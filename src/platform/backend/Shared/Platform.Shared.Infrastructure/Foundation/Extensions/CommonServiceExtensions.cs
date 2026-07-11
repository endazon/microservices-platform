using Microsoft.AspNetCore.Builder;
using Platform.Shared.Infrastructure.Foundation.Middleware;

namespace Platform.Shared.Infrastructure.Foundation.Extensions;

public static class CommonServiceExtensions
{
    public static WebApplication UsePlatformMiddleware(this WebApplication app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }
}
