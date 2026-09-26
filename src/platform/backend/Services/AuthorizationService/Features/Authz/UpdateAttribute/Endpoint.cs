using AuthorizationService.Domain;
using AuthorizationService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthorizationService.Features.Authz.UpdateAttribute;

// FR-09, UC-05: 属性辞書更新（Key / Scope は不変、許可値・重複検証。管理者のみ）。
//
// ［2026-09-27 / #1609・計画 ADR-0116 決定 3］🔴 **`department` の許可値は手で足す・消すことができない。**
// 要求の許可値は空か実効の値（realm のコード。読めなければ保存済みの値）と同じ集合のときだけ受け付ける
// （ラベル・必須は従来どおり変えられる）。保存するのは実効の値であり、realm を読めないときに保存済みの値を消さない。
public static class UpdateAttributeEndpoint
{
    public static IEndpointRouteBuilder MapUpdateAttribute(this IEndpointRouteBuilder app)
    {
        app.MapPut("/attributes/{id:guid}", async (
            Guid id, UpdateAttributeRequest req, AuthorizationDbContext db, AttributeDictionary dictionary,
            CancellationToken ct) =>
        {
            var attr = await db.AttributeDefinitions.FindAsync([id], ct);
            if (attr is null)
                return Results.NotFound();

            var existing = await db.AttributeDefinitions.ToListAsync(ct);
            var derived = DepartmentDictionaryValues.IsDerived(attr.Key);
            var reading = derived ? await dictionary.ReadDepartmentDomainAsync(ct) : DepartmentDomainReading.Unknown;
            // Key / Scope は不変。既存値を用いて一意・整合を再検証する。
            var errors = AbacValidation.ValidateAttributeDefinition(
                attr.Key, req.Label, req.AllowedValues, attr.Scope, existing, excludeId: attr.Id,
                allowedValuesDerived: derived);

            var allowedValues = req.AllowedValues;
            if (derived)
            {
                var effective = DepartmentDictionaryValues.Effective(attr.AllowedValues, reading);
                if (!DepartmentDictionaryValues.RequestAccepted(req.AllowedValues, effective))
                    errors.Add(DepartmentDictionaryValues.RejectionMessage(reading));
                allowedValues = [.. effective];
            }
            if (errors.Count > 0)
                return AuthzEndpoints.ValidationProblem(errors);

            attr.Update(req.Label, allowedValues, req.Required);
            await db.SaveChangesAsync(ct);
            return Results.Ok(AttributeDictionary.ToView(attr, reading));
        });

        return app;
    }
}
