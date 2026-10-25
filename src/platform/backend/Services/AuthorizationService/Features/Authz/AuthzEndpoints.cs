using AuthorizationService.Domain;
using AuthorizationService.Features.Authz.CreateAttribute;
using AuthorizationService.Features.Authz.CreatePolicy;
using AuthorizationService.Features.Authz.DeleteAttribute;
using AuthorizationService.Features.Authz.DeletePolicy;
using AuthorizationService.Features.Authz.GetAttribute;
using AuthorizationService.Features.Authz.GetPolicy;
using AuthorizationService.Features.Authz.ListAttributes;
using AuthorizationService.Features.Authz.ListPolicies;
using AuthorizationService.Features.Authz.SetPolicyActive;
using AuthorizationService.Features.Authz.UpdateAttribute;
using AuthorizationService.Features.Authz.UpdatePolicy;
using AuthorizationService.Features.Authz.ValidateAttributes;
using AuthorizationService.Features.Authz.ValidatePolicy;
using AuthorizationService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Extensions;

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

        // FR-05, NFR-09, 計画 ADR-0088 決定 2, ADR-0089 決定 2, [[IADR-0413]] (#1333): `/authz` の**サービス面**。
        // 🔴 **`ServiceCaller` を要求する**（利用者のトークンでは通さない。通すと confused deputy になる）。
        //
        // ［2026-10-15 / #1902・[[IADR-0533]] 追記］権限スコープ解決の REST（`POST /authz/scope`）は撤去した ——
        // 呼び出し元（BFF / AiAnalysis / Graph / Wiki / Retrieval / McpServer / DataSource）はすべて
        // east-west gRPC（`AuthzScope/Resolve`。`ResolveScope/GrpcService.cs`）へ移っており、AST も叩いていない。
        // 評価器（`AbacEvaluator.ResolveScope`）と属性の引き直し（`ScopeUserAttributeSource`）は gRPC 面が持つ。
        var services = g.MapGroup("").RequireAuthorization(PlatformAuthPolicies.ServiceCaller);
        // 計画 ADR-0089 決定 2 / IADR-0413 追記: `/authz` の**サービス面はすべて**呼び出し元サービスの資格を要求する。
        // 文書属性の辞書整合バリデーションは副作用が無く返すのは可否だけだが、値域を総当たりで推測できる口であり、
        // 「小さいから素通しでよい」を面の既定にしない（同 ADR §理由）。呼び出し元は現時点で無い（abac-seed README）。
        services.MapValidateDocumentAttributes();

        // ---- FR-09, UC-05: ABAC ポリシー・属性辞書管理（管理者のみ） ----
        // FR-09: 管理系 CRUD は管理者ロールを要求する。deny-by-default のポリシー削除・無効化を
        // 匿名で実行できないようにする。/attributes/validate はサービス間呼び出し（上の `services`）で守る。
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
    //
    // ［2026-09-27 / #1609・計画 ADR-0116 決定 3］辞書は `AttributeDictionary` から読む —— `department` の許容値は
    // realm の部門グループのコードである（seed の固定値ではない）。
    internal static async Task<List<string>> ValidatePolicyAsync(
        CreatePolicyRequest req, AuthorizationDbContext db, AttributeDictionary dictionary, CancellationToken ct)
    {
        var definitions = (await dictionary.LoadAsync(db, ct)).Definitions;
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
