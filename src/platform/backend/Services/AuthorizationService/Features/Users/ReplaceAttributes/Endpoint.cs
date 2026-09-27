using AuthorizationService.Domain;
using AuthorizationService.Domain.Ports;
using AuthorizationService.Features.Authz;
using AuthorizationService.Infrastructure.Persistence;
using Platform.Shared.Contracts.Dtos;

namespace AuthorizationService.Features.Users.ReplaceAttributes;

// SC-17: ABAC 属性の割当（差し替え）。
// 値域は SC-09 の属性辞書（`scope=user`）が持つ。**必須は部門・機密区分上限、タグは任意。**
// ［2026-09-27 / #1609・計画 ADR-0116 決定 3］辞書は `AttributeDictionary` から読む —— 部門の値域は realm の部門グループの
// コードであり、画面の選択肢（同じ辞書の一覧）と保存の検証が同じ集合を見る。
public static class ReplaceUserAttributesEndpoint
{
    public static IEndpointRouteBuilder MapReplaceUserAttributes(this IEndpointRouteBuilder app)
    {
        app.MapPut("/{userId}/attributes", async (
            string userId, ReplaceUserAttributesRequest req,
            IIdentityAdminClient identity, AuthorizationDbContext db, AttributeDictionary dictionary,
            CancellationToken ct) =>
        {
            var definitions = (await dictionary.LoadAsync(db, ct)).Definitions;
            var errors = UserAssignmentValidation.ValidateAttributes(req.Attributes, definitions);
            if (errors.Count > 0) return UserAdminEndpoints.ValidationProblem(errors);

            var updated = await identity.ReplaceAttributesAsync(userId, req.Attributes, ct);
            return updated is null ? Results.NotFound() : Results.Ok(PlatformUserMapper.ToDto(updated));
        });

        return app;
    }
}
