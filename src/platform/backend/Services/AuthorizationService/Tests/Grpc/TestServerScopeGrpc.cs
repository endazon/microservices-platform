using Grpc.Core;
using Grpc.Net.Client;
using Platform.Shared.Contracts.Dtos;
using Pb = Platform.Shared.Contracts.Grpc.Authz.V1;

namespace AuthorizationService.Tests.Grpc;

// FR-05, NFR-09, NFR-16, ADR-0089 決定 1, [[IADR-0533]] (#1902): REST の `POST /authz/scope` を撤去したので、
// スコープ解決の試験は器（`TestWebApplicationFactory` の TestServer）の上で **gRPC 面**（`AuthzScope/Resolve`）を叩く。
//
// 🔴 **HttpClient は器の口（`CreateServiceCallerClient()` / `CreateClient()`）が返したものをそのまま使う。**
// 既定のヘッダ（`TestAuthHandler` のロール）は gRPC の要求にも載るので、資格の作り分けは従前の試験のまま変わらない。
// 応答は契約 DTO（`AccessScopeResponse`）へ写して返す —— 主張（granted / filters / branches）を書き換えずに済む。
internal static class TestServerScopeGrpc
{
    /// <summary>gRPC の結末。<see cref="Scope"/> は <see cref="Status"/> が OK のときだけ在る。</summary>
    public sealed record Result(StatusCode Status, AccessScopeResponse? Scope, string? Detail = null);

    public static async Task<Result> ResolveScopeAsync(
        this HttpClient client, AccessScopeRequest request, CancellationToken ct)
    {
        using var channel = GrpcChannel.ForAddress(
            client.BaseAddress!, new GrpcChannelOptions { HttpClient = client });

        var pb = new Pb.ResolveScopeRequest { UserId = request.UserId, Action = request.Action };
        foreach (var (key, value) in request.UserAttributes)
            pb.UserAttributes[key] = value;

        try
        {
            var resp = await new Pb.AuthzScope.AuthzScopeClient(channel).ResolveAsync(pb, cancellationToken: ct);
            return new Result(StatusCode.OK, ToDto(resp));
        }
        catch (RpcException ex)
        {
            return new Result(ex.StatusCode, null, ex.Status.Detail);
        }
    }

    // proto の repeated は「無い」と「空」を区別しないので、分岐は常に（空でも）リストで返す。
    private static AccessScopeResponse ToDto(Pb.ResolveScopeResponse resp) => new(
        resp.UserId,
        [.. resp.AllowedFilters.Select(ToDto)],
        resp.Granted,
        [.. resp.Branches.Select(b => new AccessScopeBranch(b.Name, [.. b.Filters.Select(ToDto)]))]);

    private static AttributeFilter ToDto(Pb.AttributeFilter f) => new(f.Key, [.. f.AllowedValues]);
}
