using AuthorizationService.Domain;
using AuthorizationService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthorizationService.Features.Authz.UpdateAttribute;

// FR-09, UC-05: 属性辞書更新（Key / Scope は不変、許可値・重複検証。管理者のみ）。
public static class UpdateAttributeEndpoint
{
    public static IEndpointRouteBuilder MapUpdateAttribute(this IEndpointRouteBuilder app)
    {
        app.MapPut("/attributes/{id:guid}", async (
            Guid id, UpdateAttributeRequest req, AuthorizationDbContext db) =>
        {
            var attr = await db.AttributeDefinitions.FindAsync(id);
            if (attr is null)
                return Results.NotFound();

            var existing = await db.AttributeDefinitions.ToListAsync();
            // Key / Scope は不変。既存値を用いて一意・整合を再検証する。
            var errors = AbacValidation.ValidateAttributeDefinition(
                attr.Key, req.Label, req.AllowedValues, attr.Scope, existing, excludeId: attr.Id);
            if (errors.Count > 0)
                return AuthzEndpoints.ValidationProblem(errors);

            attr.Update(req.Label, req.AllowedValues, req.Required);
            await db.SaveChangesAsync();
            return Results.Ok(attr);
        });

        return app;
    }
}
