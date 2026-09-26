using AuthorizationService.Domain;
using AuthorizationService.Infrastructure.Persistence;

namespace AuthorizationService.Features.Authz.ValidateAttributes;

// FR-09: 文書属性の辞書整合バリデーション（保存前チェック用。副作用なし）。
// **サービス間呼び出しのため管理者限定にしない**（合成点で `g` 側に置く）。
// ［2026-09-27 / #1609・計画 ADR-0116 決定 3］辞書は `AttributeDictionary` から読む（`department` は realm の部門グループのコード）。
public static class ValidateDocumentAttributesEndpoint
{
    public static IEndpointRouteBuilder MapValidateDocumentAttributes(this IEndpointRouteBuilder app)
    {
        app.MapPost("/attributes/validate", async (
            ValidateDocumentAttributesRequest req, AuthorizationDbContext db, AttributeDictionary dictionary,
            CancellationToken ct) =>
        {
            var definitions = (await dictionary.LoadAsync(db, ct)).Definitions;
            var errors = AbacValidation.ValidateDocumentAttributes(req.Attributes, definitions);
            return Results.Ok(new ValidateDocumentAttributesResponse(errors.Count == 0, errors));
        });

        return app;
    }
}
