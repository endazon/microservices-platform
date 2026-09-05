using AuthorizationService.Domain;
using AuthorizationService.Infrastructure.Persistence;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Contracts.Grpc.Authz.V1;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace AuthorizationService.Features.Authz.ResolveScope;

// FR-05, NFR-09, ADR-0004, ADR-0029, ADR-0075, IADR-0379 (#1201): 権限スコープ解決の **gRPC 面**（参照実装）。
// REST の `POST /authz/scope`（Endpoint.cs）と**同じ評価器**（AbacEvaluator.ResolveScope）を呼ぶ —— 評価器を
// 2 つにしない。REST と gRPC は並走し、**並走中の正は REST** である（IADR-0379）。
//
// 🔴 **ServiceCaller を要求する。** REST の `/scope` は「サービス間呼び出しのため管理者限定にしない」として
// 認可を掛けていない（メッシュの mTLS が第一防御）。gRPC の面では**呼び出し側サービス自身の資格情報**
// （client credentials の JWT・`platform-service` ロール）を要求し、利用者のトークンでは通さない
// （通すと「利用者が直接呼んだ」と区別できず、confused deputy になる）。
// 利用者の文脈（user_id / 属性 / action）は本文で運ぶ。deny-by-default は変わらない ——
// 該当ポリシーが無ければ granted=false を**応答で**返す（エラーにしない）。
[Authorize(Policy = PlatformAuthPolicies.ServiceCaller)]
public sealed class AuthzScopeGrpcService(AuthorizationDbContext db) : AuthzScope.AuthzScopeBase
{
    public override async Task<ResolveScopeResponse> Resolve(ResolveScopeRequest request, ServerCallContext context)
    {
        // 空文字は read（REST の AccessScopeRequest.Action の既定値と同じ）。
        var action = string.IsNullOrEmpty(request.Action) ? PolicyAction.Read : request.Action;
        if (!PolicyAction.IsValid(action))
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"action は {string.Join(" / ", PolicyAction.All)} のいずれかである必要があります。"));

        var req = new AccessScopeRequest(
            request.UserId, new Dictionary<string, string>(request.UserAttributes), action);
        var policies = await db.Policies.Where(p => p.IsActive).ToListAsync(context.CancellationToken);
        var scope = AbacEvaluator.ResolveScope(req, policies, action);

        var resp = new ResolveScopeResponse { UserId = scope.UserId, Granted = scope.Granted };
        resp.AllowedFilters.AddRange(scope.AllowedFilters.Select(ToProto));
        if (scope.Branches is { Count: > 0 })
            resp.Branches.AddRange(scope.Branches.Select(b =>
            {
                var branch = new Platform.Shared.Contracts.Grpc.Authz.V1.AccessScopeBranch { Name = b.Name };
                branch.Filters.AddRange(b.Filters.Select(ToProto));
                return branch;
            }));
        return resp;
    }

    private static Platform.Shared.Contracts.Grpc.Authz.V1.AttributeFilter ToProto(
        Platform.Shared.Contracts.Dtos.AttributeFilter f)
    {
        var proto = new Platform.Shared.Contracts.Grpc.Authz.V1.AttributeFilter { Key = f.Key };
        proto.AllowedValues.AddRange(f.AllowedValues);
        return proto;
    }
}
