namespace AuthorizationService.Api.Endpoints;

public static class AuthzEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/authz").WithTags("Authorization");
        // FR-05, FR-09, UC-05, ADR-0004: ABAC スケルトン（P2 で実装）
        group.MapPost("/check", (AccessCheckRequest request) =>
            Results.Ok(new { allowed = true, reason = "stub — P2 implements ABAC" }))
            .WithName("CheckAccess");
        group.MapGet("/policies", () => Results.Ok(Array.Empty<object>()))
            .WithName("GetPolicies");
        group.MapPost("/policies", (object policy) =>
            Results.Accepted("/authz/policies/new", policy))
            .WithName("CreatePolicy");
    }
}

public record AccessCheckRequest(string UserId, string DocumentId, string Action);
