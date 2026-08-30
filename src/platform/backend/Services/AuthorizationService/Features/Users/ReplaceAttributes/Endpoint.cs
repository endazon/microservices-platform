using AuthorizationService.Domain;
using AuthorizationService.Domain.Ports;
using AuthorizationService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Contracts.Dtos;

namespace AuthorizationService.Features.Users.ReplaceAttributes;

// SC-17: ABAC 属性の割当（差し替え）。
// 値域は SC-09 の属性辞書（`scope=user`）が持つ。**必須は部門・機密区分上限、タグは任意。**
public static class ReplaceUserAttributesEndpoint
{
    public static IEndpointRouteBuilder MapReplaceUserAttributes(this IEndpointRouteBuilder app)
    {
        app.MapPut("/{userId}/attributes", async (
            string userId, ReplaceUserAttributesRequest req,
            IIdentityAdminClient identity, AuthorizationDbContext db, CancellationToken ct) =>
        {
            var definitions = await db.AttributeDefinitions.AsNoTracking().ToListAsync(ct);
            var errors = UserAssignmentValidation.ValidateAttributes(req.Attributes, definitions);
            if (errors.Count > 0) return UserAdminEndpoints.ValidationProblem(errors);

            var updated = await identity.ReplaceAttributesAsync(userId, req.Attributes, ct);
            return updated is null ? Results.NotFound() : Results.Ok(UserAdminEndpoints.ToDto(updated));
        });

        return app;
    }
}
