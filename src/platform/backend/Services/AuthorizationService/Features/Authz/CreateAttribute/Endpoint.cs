using AuthorizationService.Domain;
using AuthorizationService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthorizationService.Features.Authz.CreateAttribute;

// FR-09, UC-05: 属性辞書登録（キー重複・許可値検証。管理者のみ）。
public static class CreateAttributeEndpoint
{
    public static IEndpointRouteBuilder MapCreateAttribute(this IEndpointRouteBuilder app)
    {
        app.MapPost("/attributes", async (CreateAttributeRequest req, AuthorizationDbContext db) =>
        {
            var scope = req.Scope ?? AttributeScope.Document;
            var existing = await db.AttributeDefinitions.ToListAsync();
            var errors = AbacValidation.ValidateAttributeDefinition(
                req.Key, req.Label, req.AllowedValues, scope, existing);
            if (errors.Count > 0)
                return AuthzEndpoints.ValidationProblem(errors);

            var attr = AttributeDefinition.Create(req.Key, req.Label,
                req.AllowedValues, req.Required, scope);
            db.AttributeDefinitions.Add(attr);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // (Key, Scope) 一意制約違反。事前検証をすり抜けた同時登録（race）を 400 で返す。
                return AuthzEndpoints.ValidationProblem(
                    [$"key '{req.Key}' は scope '{scope}' に既に定義済みです。"]);
            }
            return Results.Created($"/authz/attributes/{attr.Id}", attr);
        });

        return app;
    }
}
