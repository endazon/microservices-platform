using AuthorizationService.Infrastructure.Persistence;

namespace AuthorizationService.Features.Authz.GetAttribute;

// FR-09, UC-05: 属性辞書の個別取得（管理者のみ）。
// ［2026-09-27 / #1609］一覧と同じく `AttributeDictionary` を通す（`department` は realm から導いた値と出所を返す）。
public static class GetAttributeEndpoint
{
    public static IEndpointRouteBuilder MapGetAttribute(this IEndpointRouteBuilder app)
    {
        app.MapGet("/attributes/{id:guid}", async (
            Guid id, AuthorizationDbContext db, AttributeDictionary dictionary, CancellationToken ct) =>
        {
            var snapshot = await dictionary.LoadAsync(db, ct);
            var attr = snapshot.Definitions.FirstOrDefault(d => d.Id == id);
            return attr is null ? Results.NotFound() : Results.Ok(AttributeDictionary.ToView(attr, snapshot.Reading));
        });

        return app;
    }
}
