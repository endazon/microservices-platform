using AuthorizationService.Domain;
using AuthorizationService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthorizationService.Features.Authz.DeleteAttribute;

// FR-09, UC-05, IADR-0006: 属性辞書削除（管理者のみ）。
// 既存ポリシーが当該キーを参照している場合、削除すると辞書外チェックが効かなくなり
// ポリシー条件の実効的制約が緩む。誤操作防止のため参照中は 409 で拒否する。
public static class DeleteAttributeEndpoint
{
    public static IEndpointRouteBuilder MapDeleteAttribute(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/attributes/{id:guid}", async (Guid id, AuthorizationDbContext db) =>
        {
            var attr = await db.AttributeDefinitions.FindAsync(id);
            if (attr is null)
                return Results.NotFound();

            var policies = await db.Policies.ToListAsync();
            var referencing = policies
                .Where(p => AbacValidation.PolicyReferencesAttribute(p, attr.Key, attr.Scope))
                .Select(p => p.Name)
                .ToList();
            if (referencing.Count > 0)
                return Results.Problem(
                    title: "属性辞書が参照中です",
                    detail: $"属性 '{attr.Key}' (scope={attr.Scope}) は次のポリシーが参照しているため削除できません: "
                            + string.Join(", ", referencing),
                    statusCode: StatusCodes.Status409Conflict);

            db.AttributeDefinitions.Remove(attr);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return app;
    }
}
