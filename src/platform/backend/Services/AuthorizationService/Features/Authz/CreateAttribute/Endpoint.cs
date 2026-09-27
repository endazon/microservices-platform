using AuthorizationService.Domain;
using AuthorizationService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthorizationService.Features.Authz.CreateAttribute;

// FR-09, UC-05: 属性辞書登録（キー重複・許可値検証。管理者のみ）。
//
// ［2026-09-27 / #1609・計画 ADR-0116 決定 3］🔴 **`department` の許可値は手で持たない。**
// 要求の許可値は空（＝ realm から導く）か realm のコードと同じ集合のときだけ受け付け、保存するのは realm のコードである。
// realm を読めなければ空で登録し（既存の値は無い）、応答の出所を「不明」とする —— 次に realm を読めた要求で埋まる。
public static class CreateAttributeEndpoint
{
    public static IEndpointRouteBuilder MapCreateAttribute(this IEndpointRouteBuilder app)
    {
        app.MapPost("/attributes", async (
            CreateAttributeRequest req, AuthorizationDbContext db, AttributeDictionary dictionary, CancellationToken ct) =>
        {
            var scope = req.Scope ?? AttributeScope.Document;
            var existing = await db.AttributeDefinitions.ToListAsync(ct);
            var derived = DepartmentDictionaryValues.IsDerived(req.Key);
            var reading = derived ? await dictionary.ReadDepartmentDomainAsync(ct) : DepartmentDomainReading.Unknown;
            var errors = AbacValidation.ValidateAttributeDefinition(
                req.Key, req.Label, req.AllowedValues, scope, existing, allowedValuesDerived: derived);

            var allowedValues = req.AllowedValues;
            if (derived)
            {
                var effective = DepartmentDictionaryValues.Effective(null, reading);
                if (!DepartmentDictionaryValues.RequestAccepted(req.AllowedValues, effective))
                    errors.Add(DepartmentDictionaryValues.RejectionMessage(reading));
                allowedValues = [.. effective];
            }
            if (errors.Count > 0)
                return AuthzEndpoints.ValidationProblem(errors);

            var attr = AttributeDefinition.Create(req.Key, req.Label,
                allowedValues, req.Required, scope);
            db.AttributeDefinitions.Add(attr);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // (Key, Scope) 一意制約違反。事前検証をすり抜けた同時登録（race）を 400 で返す。
                return AuthzEndpoints.ValidationProblem(
                    [$"key '{req.Key}' は scope '{scope}' に既に定義済みです。"]);
            }
            return Results.Created($"/authz/attributes/{attr.Id}", AttributeDictionary.ToView(attr, reading));
        });

        return app;
    }
}
