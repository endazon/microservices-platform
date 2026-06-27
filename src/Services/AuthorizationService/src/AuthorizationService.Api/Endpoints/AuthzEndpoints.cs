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

        // FR-09: ポリシー一覧
        g.MapGet("/policies", async (AuthorizationDbContext db) =>
            Results.Ok(await db.Policies.ToListAsync()));

        // FR-09: ポリシー登録
        g.MapPost("/policies", async (CreatePolicyRequest req, AuthorizationDbContext db) =>
        {
            var policy = AbacPolicy.Create(req.Name, req.Action,
                req.UserConditions, req.DocumentConditions);
            db.Policies.Add(policy);
            await db.SaveChangesAsync();
            return Results.Created($"/authz/policies/{policy.Id}", policy);
        });

        // FR-09: 属性辞書一覧
        g.MapGet("/attributes", async (AuthorizationDbContext db) =>
            Results.Ok(await db.AttributeDefinitions.ToListAsync()));

        // FR-09: 属性辞書登録
        g.MapPost("/attributes", async (CreateAttributeRequest req, AuthorizationDbContext db) =>
        {
            var attr = AttributeDefinition.Create(req.Key, req.Label,
                req.AllowedValues, req.Required, req.Scope ?? AttributeScope.Document);
            db.AttributeDefinitions.Add(attr);
            await db.SaveChangesAsync();
            return Results.Created($"/authz/attributes/{attr.Id}", attr);
        });

        return app;
    }
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
