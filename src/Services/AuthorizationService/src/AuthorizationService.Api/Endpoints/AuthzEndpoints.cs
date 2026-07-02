using AuthorizationService.Api.Domain;
using AuthorizationService.Api.Infrastructure;
using AuthorizationService.Api.Services;
using KnowledgePlatform.Shared.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace AuthorizationService.Api.Endpoints;

// FR-05, FR-09, UC-05, ADR-0004: ABAC エンドポイント
public static class AuthzEndpoints
{
    public static IEndpointRouteBuilder MapAuthzEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/authz").WithTags("Authorization");

        // FR-05: 権限スコープ解決（検索・RAG の前に呼び出される）
        g.MapPost("/scope", async (AccessScopeRequest req, AuthorizationDbContext db) =>
        {
            var policies = await db.Policies.Where(p => p.IsActive).ToListAsync();
            var scope = AbacEvaluator.ResolveScope(req, policies, PolicyAction.Read);
            return Results.Ok(scope);
        });

        // ---- FR-09, UC-05: ABAC ポリシー管理 ----

        // ポリシー一覧
        g.MapGet("/policies", async (AuthorizationDbContext db) =>
            Results.Ok(await db.Policies.ToListAsync()));

        // ポリシー個別取得
        g.MapGet("/policies/{id:guid}", async (Guid id, AuthorizationDbContext db) =>
        {
            var policy = await db.Policies.FindAsync(id);
            return policy is null ? Results.NotFound() : Results.Ok(policy);
        });

        // ポリシー登録（保存前に矛盾検証）
        g.MapPost("/policies", async (CreatePolicyRequest req, AuthorizationDbContext db) =>
        {
            var definitions = await db.AttributeDefinitions.ToListAsync();
            var errors = AbacValidation.ValidatePolicy(
                req.Name, req.Action, req.UserConditions, req.DocumentConditions, definitions);
            if (errors.Count > 0)
                return ValidationProblem(errors);

            var policy = AbacPolicy.Create(req.Name, req.Action,
                req.UserConditions, req.DocumentConditions);
            db.Policies.Add(policy);
            await db.SaveChangesAsync();
            return Results.Created($"/authz/policies/{policy.Id}", policy);
        });

        // ポリシー更新（保存前に矛盾検証）
        g.MapPut("/policies/{id:guid}", async (Guid id, CreatePolicyRequest req, AuthorizationDbContext db) =>
        {
            var policy = await db.Policies.FindAsync(id);
            if (policy is null)
                return Results.NotFound();

            var definitions = await db.AttributeDefinitions.ToListAsync();
            var errors = AbacValidation.ValidatePolicy(
                req.Name, req.Action, req.UserConditions, req.DocumentConditions, definitions);
            if (errors.Count > 0)
                return ValidationProblem(errors);

            policy.Update(req.Name, req.Action, req.UserConditions, req.DocumentConditions);
            await db.SaveChangesAsync();
            return Results.Ok(policy);
        });

        // ポリシー有効／無効の切替（削除せず一時停止）
        g.MapPatch("/policies/{id:guid}/active", async (Guid id, SetActiveRequest req, AuthorizationDbContext db) =>
        {
            var policy = await db.Policies.FindAsync(id);
            if (policy is null)
                return Results.NotFound();

            policy.SetActive(req.IsActive);
            await db.SaveChangesAsync();
            return Results.Ok(policy);
        });

        // ポリシー削除
        g.MapDelete("/policies/{id:guid}", async (Guid id, AuthorizationDbContext db) =>
        {
            var policy = await db.Policies.FindAsync(id);
            if (policy is null)
                return Results.NotFound();

            db.Policies.Remove(policy);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // ---- FR-09, UC-05: 属性辞書管理 ----

        // 属性辞書一覧
        g.MapGet("/attributes", async (AuthorizationDbContext db) =>
            Results.Ok(await db.AttributeDefinitions.ToListAsync()));

        // 属性辞書個別取得
        g.MapGet("/attributes/{id:guid}", async (Guid id, AuthorizationDbContext db) =>
        {
            var attr = await db.AttributeDefinitions.FindAsync(id);
            return attr is null ? Results.NotFound() : Results.Ok(attr);
        });

        // 属性辞書登録（キー重複・許可値検証）
        g.MapPost("/attributes", async (CreateAttributeRequest req, AuthorizationDbContext db) =>
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
            await db.SaveChangesAsync();
            return Results.Created($"/authz/attributes/{attr.Id}", attr);
        });

        // 属性辞書更新（Key / Scope は不変、許可値・重複検証）
        g.MapPut("/attributes/{id:guid}", async (Guid id, UpdateAttributeRequest req, AuthorizationDbContext db) =>
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
        g.MapDelete("/attributes/{id:guid}", async (Guid id, AuthorizationDbContext db) =>
        {
            var attr = await db.AttributeDefinitions.FindAsync(id);
            if (attr is null)
                return Results.NotFound();

            db.AttributeDefinitions.Remove(attr);
            await db.SaveChangesAsync();
            return Results.NoContent();
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
