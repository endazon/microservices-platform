using AuthorizationService.Domain;
using AuthorizationService.Infrastructure.Persistence;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Microsoft.EntityFrameworkCore;

namespace AuthorizationService.Features.Authz;

// FR-05, FR-09, UC-05, ADR-0004: ABAC エンドポイント
public static class AuthzEndpoints
{
    public static IEndpointRouteBuilder MapAuthzEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/authz").WithTags("Authorization");

        // FR-05: 権限スコープ解決（検索・RAG の前に呼び出される）
        //
        // FR-21, ADR-0036 D-07, IADR-0253 決定 5（2026-08-23 改定 / #989）: 解決するアクションは
        // 要求の Action（既定 read）。従前ここで PolicyAction.Read をハードコードしており、
        // 書き込みの認可スコープをこの経路で出せなかった。
        //
        // **不正なアクションは 400 で返す。** 黙って空スコープへ写すと、呼び出し側の設定誤りが
        // 「常に全件遮断」として沈黙する。既知の全呼び出し元は非 2xx を Granted=false へ
        // 縮退させるため、400 でも deny 側へ倒れる（緩む向きの縮退ではない）。
        g.MapPost("/scope", async (AccessScopeRequest req, AuthorizationDbContext db) =>
        {
            if (!PolicyAction.IsValid(req.Action))
                return ValidationProblem(
                    [$"action は {string.Join(" / ", PolicyAction.All)} のいずれかである必要があります。"]);

            var policies = await db.Policies.Where(p => p.IsActive).ToListAsync();
            var scope = AbacEvaluator.ResolveScope(req, policies, req.Action);
            return Results.Ok(scope);
        });

        // ---- FR-09, UC-05: ABAC ポリシー・属性辞書管理（管理者のみ） ----
        // FR-09: 管理系 CRUD は管理者ロールを要求する。deny-by-default のポリシー削除・無効化を
        // 匿名で実行できないようにする。/scope・/attributes/validate はサービス間呼び出しのため対象外。
        var admin = g.MapGroup("").RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        // ポリシー一覧
        admin.MapGet("/policies", async (AuthorizationDbContext db) =>
            Results.Ok(await db.Policies.ToListAsync()));

        // ポリシー個別取得
        admin.MapGet("/policies/{id:guid}", async (Guid id, AuthorizationDbContext db) =>
        {
            var policy = await db.Policies.FindAsync(id);
            return policy is null ? Results.NotFound() : Results.Ok(policy);
        });

        // ポリシー登録（保存前に矛盾検証）
        admin.MapPost("/policies", async (CreatePolicyRequest req, AuthorizationDbContext db) =>
        {
            var errors = await ValidatePolicyAsync(req, db);
            if (errors.Count > 0)
                return ValidationProblem(errors);

            var policy = AbacPolicy.Create(req.Name, req.Action,
                req.UserConditions, req.DocumentConditions);
            db.Policies.Add(policy);
            await db.SaveChangesAsync();
            return Results.Created($"/authz/policies/{policy.Id}", policy);
        });

        // ポリシー更新（保存前に矛盾検証）
        admin.MapPut("/policies/{id:guid}", async (Guid id, CreatePolicyRequest req, AuthorizationDbContext db) =>
        {
            var policy = await db.Policies.FindAsync(id);
            if (policy is null)
                return Results.NotFound();

            var errors = await ValidatePolicyAsync(req, db);
            if (errors.Count > 0)
                return ValidationProblem(errors);

            policy.Update(req.Name, req.Action, req.UserConditions, req.DocumentConditions);
            await db.SaveChangesAsync();
            return Results.Ok(policy);
        });

        // ポリシー有効／無効の切替（削除せず一時停止）
        admin.MapPatch("/policies/{id:guid}/active", async (Guid id, SetActiveRequest req, AuthorizationDbContext db) =>
        {
            var policy = await db.Policies.FindAsync(id);
            if (policy is null)
                return Results.NotFound();

            policy.SetActive(req.IsActive);
            await db.SaveChangesAsync();
            return Results.Ok(policy);
        });

        // ポリシー削除
        admin.MapDelete("/policies/{id:guid}", async (Guid id, AuthorizationDbContext db) =>
        {
            var policy = await db.Policies.FindAsync(id);
            if (policy is null)
                return Results.NotFound();

            db.Policies.Remove(policy);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // ---- FR-09, UC-05: 属性辞書管理（管理者のみ） ----

        // 属性辞書一覧
        admin.MapGet("/attributes", async (AuthorizationDbContext db) =>
            Results.Ok(await db.AttributeDefinitions.ToListAsync()));

        // 属性辞書個別取得
        admin.MapGet("/attributes/{id:guid}", async (Guid id, AuthorizationDbContext db) =>
        {
            var attr = await db.AttributeDefinitions.FindAsync(id);
            return attr is null ? Results.NotFound() : Results.Ok(attr);
        });

        // 属性辞書登録（キー重複・許可値検証）
        admin.MapPost("/attributes", async (CreateAttributeRequest req, AuthorizationDbContext db) =>
        {
            var scope = req.Scope ?? AttributeScope.Document;
            var existing = await db.AttributeDefinitions.ToListAsync();
            var errors = AbacValidation.ValidateAttributeDefinition(
                req.Key, req.Label, req.AllowedValues, scope, existing);
            if (errors.Count > 0)
                return ValidationProblem(errors);

            var attr = AttributeDefinition.Create(req.Key, req.Label,
                req.AllowedValues, req.Required, scope);
            db.AttributeDefinitions.Add(attr);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // (Key, Scope) 一意制約違反。事前検証をすり抜けた同時登録（race）を 400 で返す。
                return ValidationProblem([$"key '{req.Key}' は scope '{scope}' に既に定義済みです。"]);
            }
            return Results.Created($"/authz/attributes/{attr.Id}", attr);
        });

        // 属性辞書更新（Key / Scope は不変、許可値・重複検証）
        admin.MapPut("/attributes/{id:guid}", async (Guid id, UpdateAttributeRequest req, AuthorizationDbContext db) =>
        {
            var attr = await db.AttributeDefinitions.FindAsync(id);
            if (attr is null)
                return Results.NotFound();

            var existing = await db.AttributeDefinitions.ToListAsync();
            // Key / Scope は不変。既存値を用いて一意・整合を再検証する。
            var errors = AbacValidation.ValidateAttributeDefinition(
                attr.Key, req.Label, req.AllowedValues, attr.Scope, existing, excludeId: attr.Id);
            if (errors.Count > 0)
                return ValidationProblem(errors);

            attr.Update(req.Label, req.AllowedValues, req.Required);
            await db.SaveChangesAsync();
            return Results.Ok(attr);
        });

        // 属性辞書削除
        // FR-09, IADR-0006: 既存ポリシーが当該キーを参照している場合、削除すると辞書外チェックが
        // 効かなくなりポリシー条件の実効的制約が緩む。誤操作防止のため参照中は 409 で拒否する。
        admin.MapDelete("/attributes/{id:guid}", async (Guid id, AuthorizationDbContext db) =>
        {
            var attr = await db.AttributeDefinitions.FindAsync(id);
            if (attr is null)
                return Results.NotFound();

            var policies = await db.Policies.ToListAsync();
            var referencing = policies
                .Where(p => AbacValidation.PolicyReferencesAttribute(p, attr.Key, attr.Scope))
                .Select(p => p.Name)
                .ToList();
            if (referencing.Count > 0)
                return Results.Problem(
                    title: "属性辞書が参照中です",
                    detail: $"属性 '{attr.Key}' (scope={attr.Scope}) は次のポリシーが参照しているため削除できません: "
                            + string.Join(", ", referencing),
                    statusCode: StatusCodes.Status409Conflict);

            db.AttributeDefinitions.Remove(attr);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // FR-05, FR-09, SC-09, #535: ポリシーの dry-run 検証（保存せず検証だけ行う。副作用なし）。
        //
        // 計画確定（2026-08-05・裁定 Q23）:「**保存せず検証だけ行う口を定める**。従前は検証が
        // `POST /policies` の応答（400）としてのみ得られ、hi-fi が保存とは別に描く『検証』ボタンを
        // 満たせなかった。**検証ロジックは既にあるため、保存せず同じ検証を走らせる口を足すだけで足りる**。」
        //
        // **`ValidatePolicyAsync` を保存経路と共有しているのが本エンドポイントの要点である。**
        // 計画は「ローカルでの代用は採らない——『検証は通ったのに保存で矛盾が出る』形になり、
        // 検証ボタンへの信頼が失われる。**信頼できない検証ボタンは無いより悪い**」と書いている。
        // **その一致をコメントではなく構造で守る**（3 経路が同じ 1 つの関数を呼ぶ）。
        //
        // **200 で返す。** 矛盾が見つかったことは要求の失敗ではない（保存は従来どおり 400 ＋ RFC7807）。
        // **要求型は `CreatePolicyRequest` を再利用する**——画面が保存用と検証用で 2 つの組み立てを
        // 持つと、そこがズレる余地になる。
        //
        // **管理者限定**は `admin` グループが担う（[[IADR-0040]] 決定 2）。
        admin.MapPost("/policies/validate", async (CreatePolicyRequest req, AuthorizationDbContext db) =>
        {
            var errors = await ValidatePolicyAsync(req, db);
            return Results.Ok(new ValidatePolicyResponse(errors.Count == 0, errors));
        });

        // 文書属性の辞書整合バリデーション（保存前チェック用。副作用なし）
        g.MapPost("/attributes/validate", async (ValidateDocumentAttributesRequest req, AuthorizationDbContext db) =>
        {
            var definitions = await db.AttributeDefinitions.ToListAsync();
            var errors = AbacValidation.ValidateDocumentAttributes(req.Attributes, definitions);
            return Results.Ok(new ValidateDocumentAttributesResponse(errors.Count == 0, errors));
        });

        return app;
    }

    // FR-09, SC-09, #535: ポリシーの矛盾検証。**保存（POST / PUT）と dry-run の 3 経路が
    // この 1 つを呼ぶ。**
    //
    // 従前は同じ 3 行が `POST /policies` と `PUT /policies/{id}` に**重複していた**。
    // dry-run を 3 つ目の複製として足すと、**将来どれか 1 つだけを直したときに黙ってズレ**、
    // 計画が名指しで禁じた事態（「検証は通ったのに保存で矛盾が出る」）が構造的に可能になる。
    // **括り出しは抽象化のためではなく、計画が求めた一致を構造で守るためである。**
    private static async Task<List<string>> ValidatePolicyAsync(
        CreatePolicyRequest req, AuthorizationDbContext db)
    {
        var definitions = await db.AttributeDefinitions.ToListAsync();
        return AbacValidation.ValidatePolicy(
            req.Name, req.Action, req.UserConditions, req.DocumentConditions, definitions);
    }

    // RFC7807 準拠のバリデーションエラー（400）。エラー一覧を errors キーへ束ねる。
    private static IResult ValidationProblem(List<string> errors) =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["errors"] = errors.ToArray()
        });
}

public record CreatePolicyRequest(
    string Name,
    string Action,
    Dictionary<string, List<string>> UserConditions,
    Dictionary<string, List<string>> DocumentConditions);

public record CreateAttributeRequest(
    string Key,
    string Label,
    List<string> AllowedValues,
    bool Required,
    string? Scope);

// 属性辞書更新（Key / Scope は不変のため受け取らない）
public record UpdateAttributeRequest(
    string Label,
    List<string> AllowedValues,
    bool Required);

public record SetActiveRequest(bool IsActive);

public record ValidateDocumentAttributesRequest(Dictionary<string, string> Attributes);

public record ValidateDocumentAttributesResponse(bool Valid, List<string> Errors);
