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

        // FR-05, NFR-09, 計画 ADR-0088 決定 2, [[IADR-0379]] 決定 4, [[IADR-0413]] (#1333):
        // 権限スコープ解決（検索・RAG の前に呼び出される）。
        //
        // 🔴 **`ServiceCaller` を要求する。gRPC 面と同じ 1 つのポリシーである。**
        // 従前ここは**認可を 1 つも掛けていなかった**（「サービス間呼び出しのため管理者限定にしない」
        // と書いてあったが、**管理者限定にしない**ことと**誰でも通す**ことは同じではない）。
        // 🔴 **管理者限定にはしない** —— これはサービスが呼ぶ面であり、利用者のトークンでは通さない
        // （通すと呼び出し先が「利用者が直接呼んだ」と区別できず confused deputy になる）。
        //
        // 🔴 **これは `ADR-0088` 決定 1（属性の引き直し）の着地の条件である。**
        // 引き直しだけを入れて無認可のまま残すと、この端点は**今より危険になる** ——
        // `user_id` を渡すだけで**その利用者の真の属性に基づく判定**が引け、
        // **任意利用者の ABAC 属性のオラクル**になる（同 実測 6）。**2 つは同じ着地に含める。**
        //
        // 呼び出し元 4 つ（BFF / AiAnalysis / Graph / Wiki）は s2s の資格情報を
        // **compose・helm・realm のいずれにも既に持っている**（作業仕様書 §実測 4）。
        var services = g.MapGroup("").RequireAuthorization(PlatformAuthPolicies.ServiceCaller);
        services.MapResolveScope();
        // 計画 ADR-0089 決定 2 / IADR-0413 追記: `/authz` の**サービス面はすべて**呼び出し元サービスの資格を要求する。
        // 文書属性の辞書整合バリデーションは副作用が無く返すのは可否だけだが、値域を総当たりで推測できる口であり、
        // 「小さいから素通しでよい」を面の既定にしない（同 ADR §理由）。呼び出し元は現時点で無い（abac-seed README）。
        services.MapValidateDocumentAttributes();

        // ---- FR-09, UC-05: ABAC ポリシー・属性辞書管理（管理者のみ） ----
        // FR-09: 管理系 CRUD は管理者ロールを要求する。deny-by-default のポリシー削除・無効化を
        // 匿名で実行できないようにする。/scope・/attributes/validate はサービス間呼び出し（上の `services`）で守る。
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
