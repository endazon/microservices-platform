using KnowledgePlatform.Shared.Contracts.Dtos;

namespace WikiService.Api.Foundation.Ports;

// FR-13, FR-05, UC-07: 閲覧要求元の利用者属性から ABAC 許可スコープを解決する。
public interface IWikiAccessResolver
{
    Task<AccessScopeResponse> ResolveAsync(HttpContext ctx, CancellationToken ct = default);
}
