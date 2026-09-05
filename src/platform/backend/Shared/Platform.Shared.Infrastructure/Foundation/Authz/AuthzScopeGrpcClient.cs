using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Dto = Platform.Shared.Contracts.Dtos;
using Pb = Platform.Shared.Contracts.Grpc.Authz.V1;

namespace Platform.Shared.Infrastructure.Foundation.Authz;

// FR-05, NFR-09, ADR-0029, ADR-0075, IADR-0379 (#1201): 認可スコープ解決の **gRPC 経路**（参照実装）。
// REST 経路（BffScopeResolver の HTTP）と同じ deny-by-default で縮退する ——
// UNAUTHENTICATED / PERMISSION_DENIED / UNAVAILABLE / s2s トークン取得失敗 のいずれも null（閲覧可能なし）。
//
// **並走中の正は REST である。** 本クライアントは `Services:AuthorizationServiceGrpc` が構成されたときだけ
// 登録され（AddAuthzScopeGrpcClient）、BffScopeResolver は登録が在ればこちらを使う。
public sealed class AuthzScopeGrpcClient(
    Pb.AuthzScope.AuthzScopeClient client,
    ILogger<AuthzScopeGrpcClient> logger)
{
    public const string AddressKey = "Services:AuthorizationServiceGrpc";

    public async Task<BffAccessScope?> ResolveAsync(
        string userId, IReadOnlyDictionary<string, string> userAttributes, string action, CancellationToken ct)
    {
        var request = new Pb.ResolveScopeRequest { UserId = userId, Action = action };
        foreach (var (key, value) in userAttributes)
            request.UserAttributes[key] = value;

        try
        {
            var resp = await client.ResolveAsync(request, cancellationToken: ct);
            if (!resp.Granted)
                return null;

            return new BffAccessScope(
                resp.AllowedFilters.Select(ToFilter).ToList(),
                resp.Granted,
                resp.Branches.Count == 0
                    ? null
                    : resp.Branches.Select(b => new Dto.AccessScopeBranch(b.Name, b.Filters.Select(ToFilter).ToList())).ToList());
        }
        catch (RpcException ex)
        {
            // 認可サービス不調・資格情報不備は deny-by-default（null）へ縮退する。
            logger.LogWarning(
                "認可スコープの gRPC 解決に失敗しました（{Status}）。閲覧可能なしへ縮退します。", ex.StatusCode);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            // s2s トークンが取れない（構成不備・IdP 不達）。匿名では呼ばず deny へ倒す。
            logger.LogWarning(ex, "s2s トークンが取得できないため認可スコープを解決できません。閲覧可能なしへ縮退します。");
            return null;
        }
    }

    private static Dto.AttributeFilter ToFilter(Pb.AttributeFilter f) => new(f.Key, f.AllowedValues.ToList());
}

public static class AuthzScopeGrpcClientExtensions
{
    // `Services:AuthorizationServiceGrpc`（h2c のアドレス。例: http://authorization-service:8081）が
    // 構成されたときだけ gRPC 経路を登録する。未設定なら何も登録せず、BffScopeResolver は REST のまま。
    public static IServiceCollection AddAuthzScopeGrpcClient(this IServiceCollection services, IConfiguration config)
    {
        var address = config[AuthzScopeGrpcClient.AddressKey];
        if (string.IsNullOrWhiteSpace(address))
            return services;

        services.AddPlatformServiceToken(config);
        services.AddSingleton(sp =>
            GrpcClientExtensions.CreatePlatformChannel(address, sp.GetRequiredService<IServiceTokenProvider>()));
        services.AddSingleton(sp =>
            new Pb.AuthzScope.AuthzScopeClient(sp.GetRequiredService<global::Grpc.Net.Client.GrpcChannel>()));
        services.AddSingleton<AuthzScopeGrpcClient>();
        return services;
    }
}
