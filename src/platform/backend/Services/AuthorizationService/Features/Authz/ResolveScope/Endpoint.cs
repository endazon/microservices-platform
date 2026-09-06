using AuthorizationService.Domain;
using AuthorizationService.Infrastructure.Persistence;
using FluentValidation;
using Platform.Shared.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace AuthorizationService.Features.Authz.ResolveScope;

// FR-05, UC-05, ADR-0004: 権限スコープ解決（検索・RAG の前に呼び出される）。
//
// FR-21, ADR-0036 D-07, IADR-0253 決定 5（2026-08-23 改定 / #989）: 解決するアクションは
// 要求の Action（既定 read）。従前ここで PolicyAction.Read をハードコードしており、
// 書き込みの認可スコープをこの経路で出せなかった。
//
// **不正なアクションは 400 で返す。** 黙って空スコープへ写すと、呼び出し側の設定誤りが
// 「常に全件遮断」として沈黙する。既知の全呼び出し元は非 2xx を Granted=false へ
// 縮退させるため、400 でも deny 側へ倒れる（緩む向きの縮退ではない）。
public static class ResolveScopeEndpoint
{
    public static IEndpointRouteBuilder MapResolveScope(this IEndpointRouteBuilder app)
    {
        app.MapPost("/scope", async (AccessScopeRequest req,
            IValidator<AccessScopeRequest> validator, AuthorizationDbContext db) =>
        {
            // FR-05, FR-21 / 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 /
            // [[IADR-0398]] 決定 1 (b): 値域は `ResolveScopeValidator` が持つ。
            // **鍵（`errors`）は sink が持つ**ので、端点は先頭 1 件の**本文だけ**を渡す
            // （移送前もガード節 1 本が 1 要素のコレクション式を渡していた ＝ 形 α）。
            // 🔴 **この呼び出しはポリシー照会より前**でなければならない（従前のガード節と同じ位置）。
            var gate = validator.Validate(req);
            if (!gate.IsValid) return AuthzEndpoints.ValidationProblem([gate.Errors[0].ErrorMessage]);

            var policies = await db.Policies.Where(p => p.IsActive).ToListAsync();
            var scope = AbacEvaluator.ResolveScope(req, policies, req.Action);
            return Results.Ok(scope);
        });

        return app;
    }
}
