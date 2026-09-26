using AuthorizationService.Infrastructure.Persistence;

namespace AuthorizationService.Features.Authz.ListAttributes;

// FR-09, UC-05: 属性辞書一覧（管理者のみ）。
// ［2026-09-27 / #1609・計画 ADR-0116 決定 3］`department` の許可値は realm の部門グループから導き、
// 出所（`allowedValuesSource`）を添えて返す。realm を読めなければ保存済みの値を「不明」として返す（消さない）。
public static class ListAttributesEndpoint
{
    public static IEndpointRouteBuilder MapListAttributes(this IEndpointRouteBuilder app)
    {
        app.MapGet("/attributes", async (AuthorizationDbContext db, AttributeDictionary dictionary, CancellationToken ct) =>
            Results.Ok((await dictionary.LoadAsync(db, ct)).Views()));

        return app;
    }
}
