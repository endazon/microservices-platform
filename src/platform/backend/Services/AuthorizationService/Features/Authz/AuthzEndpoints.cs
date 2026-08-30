using AuthorizationService.Domain;
using AuthorizationService.Features.Authz.CreateAttribute;
using AuthorizationService.Features.Authz.CreatePolicy;
using AuthorizationService.Features.Authz.DeleteAttribute;
using AuthorizationService.Features.Authz.DeletePolicy;
using AuthorizationService.Features.Authz.GetAttribute;
using AuthorizationService.Features.Authz.GetPolicy;
using AuthorizationService.Features.Authz.ListAttributes;
using AuthorizationService.Features.Authz.ListPolicies;
using AuthorizationService.Features.Authz.ResolveScope;
using AuthorizationService.Features.Authz.SetPolicyActive;
using AuthorizationService.Features.Authz.UpdateAttribute;
using AuthorizationService.Features.Authz.UpdatePolicy;
using AuthorizationService.Features.Authz.ValidateAttributes;
using AuthorizationService.Features.Authz.ValidatePolicy;
using AuthorizationService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Microsoft.EntityFrameworkCore;

namespace AuthorizationService.Features.Authz;

// FR-05, FR-09, UC-05, ADR-0004: ABAC スライスの合成点。
//
// ADR-0065 決定 2: 1 ユースケースのファイルは操作フォルダへ束ねる。
// **本ファイルに残すのは、グループ（/authz と管理者限定サブグループ）の構築と、
// 複数操作が共有するヘルパだけである。**
public static class AuthzEndpoints
{
    public static IEndpointRouteBuilder MapAuthzEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/authz").WithTags("Authorization");

        // FR-05: 権限スコープ解決（検索・RAG の前に呼び出される）。**サービス間呼び出しのため管理者限定にしない。**
        g.MapResolveScope();

        // ---- FR-09, UC-05: ABAC ポリシー・属性辞書管理（管理者のみ） ----
        // FR-09: 管理系 CRUD は管理者ロールを要求する。deny-by-default のポリシー削除・無効化を
        // 匿名で実行できないようにする。/scope・/attributes/validate はサービス間呼び出しのため対象外。
        var admin = g.MapGroup("").RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        admin.MapListPolicies();
        admin.MapGetPolicy();
        admin.MapCreatePolicy();
        admin.MapUpdatePolicy();
        admin.MapSetPolicyActive();
        admin.MapDeletePolicy();

        admin.MapListAttributes();
        admin.MapGetAttribute();
        admin.MapCreateAttribute();
        admin.MapUpdateAttribute();
        admin.MapDeleteAttribute();

        // FR-05, FR-09, SC-09, #535: ポリシーの dry-run 検証。**管理者限定**は `admin` グループが担う
        // （[[IADR-0040]] 決定 2）。
        admin.MapValidatePolicy();

        // 文書属性の辞書整合バリデーション（保存前チェック用。副作用なし）。
        g.MapValidateDocumentAttributes();

        return app;
    }

    // FR-09, SC-09, #535: ポリシーの矛盾検証。**保存（POST / PUT）と dry-run の 3 経路が
    // この 1 つを呼ぶ。**
    //
    // 従前は同じ 3 行が `POST /policies` と `PUT /policies/{id}` に**重複していた**。
    // dry-run を 3 つ目の複製として足すと、**将来どれか 1 つだけを直したときに黙ってズレ**、
    // 計画が名指しで禁じた事態（「検証は通ったのに保存で矛盾が出る」）が構造的に可能になる。
    // **括り出しは抽象化のためではなく、計画が求めた一致を構造で守るためである。**
    //
    // 🔴 **ADR-0065 決定 2 の 3 段化でも、この 1 つを操作フォルダへ複製してはならない**
    // （3 操作が同じ 1 つを呼ぶことが計画 #535 の要件そのものである）。集約直下に残す。
    internal static async Task<List<string>> ValidatePolicyAsync(
        CreatePolicyRequest req, AuthorizationDbContext db)
    {
        var definitions = await db.AttributeDefinitions.ToListAsync();
        return AbacValidation.ValidatePolicy(
            req.Name, req.Action, req.UserConditions, req.DocumentConditions, definitions);
    }

    // RFC7807 準拠のバリデーションエラー（400）。エラー一覧を errors キーへ束ねる。
    internal static IResult ValidationProblem(List<string> errors) =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["errors"] = errors.ToArray()
        });
}
