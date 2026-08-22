using Platform.Shared.Contracts.Dtos;

namespace GraphService.Api.Foundation.Ports;

// FR-17, FR-05, UC-10: 探索要求元の利用者属性から ABAC 許可スコープを解決する。
// WikiService の IWikiAccessResolver と同型。
public interface IGraphAccessResolver
{
    Task<AccessScopeResponse> ResolveAsync(HttpContext ctx, CancellationToken ct = default);
}
