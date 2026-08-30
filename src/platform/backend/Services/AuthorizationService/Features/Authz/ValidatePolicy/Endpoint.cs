using AuthorizationService.Infrastructure.Persistence;
using Platform.Shared.Contracts.Dtos;

namespace AuthorizationService.Features.Authz.ValidatePolicy;

// FR-05, FR-09, SC-09, #535: ポリシーの dry-run 検証（保存せず検証だけ行う。副作用なし）。
//
// 計画確定（2026-08-05・裁定 Q23）:「**保存せず検証だけ行う口を定める**。従前は検証が
// `POST /policies` の応答（400）としてのみ得られ、hi-fi が保存とは別に描く『検証』ボタンを
// 満たせなかった。**検証ロジックは既にあるため、保存せず同じ検証を走らせる口を足すだけで足りる**。」
//
// **`AuthzEndpoints.ValidatePolicyAsync` を保存経路と共有しているのが本エンドポイントの要点である。**
// 計画は「ローカルでの代用は採らない——『検証は通ったのに保存で矛盾が出る』形になり、
// 検証ボタンへの信頼が失われる。**信頼できない検証ボタンは無いより悪い**」と書いている。
// **その一致をコメントではなく構造で守る**（3 経路が同じ 1 つの関数を呼ぶ）。
//
// **200 で返す。** 矛盾が見つかったことは要求の失敗ではない（保存は従来どおり 400 ＋ RFC7807）。
// **要求型は `CreatePolicyRequest` を再利用する**——画面が保存用と検証用で 2 つの組み立てを
// 持つと、そこがズレる余地になる。
//
// **管理者限定**は合成点の `admin` グループが担う（[[IADR-0040]] 決定 2）。
public static class ValidatePolicyEndpoint
{
    public static IEndpointRouteBuilder MapValidatePolicy(this IEndpointRouteBuilder app)
    {
        app.MapPost("/policies/validate", async (CreatePolicyRequest req, AuthorizationDbContext db) =>
        {
            var errors = await AuthzEndpoints.ValidatePolicyAsync(req, db);
            return Results.Ok(new ValidatePolicyResponse(errors.Count == 0, errors));
        });

        return app;
    }
}
