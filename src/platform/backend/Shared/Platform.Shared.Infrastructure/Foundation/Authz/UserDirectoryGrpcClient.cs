using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Pb = Platform.Shared.Contracts.Grpc.Authz.V1;

namespace Platform.Shared.Infrastructure.Foundation.Authz;

// FR-05, FR-16, UC-04, UC-09, SC-06, SC-12, NFR-09, ADR-0004, ADR-0062, ADR-0064, ADR-0074,
// ADR-0029, ADR-0075, IADR-0329, IADR-0379, IADR-0385, IADR-0401 (#1255):
// 利用者名簿の**狭い読み口**（`platform.authz.v1.UserDirectory`）の呼び出し側。
//
// 🔴 **列挙の口は無い。** REST の `GET /authz/users`（AdminOnly・全件列挙）は s2s の面へ出していない
// （IADR-0401 決定 2）。呼び出し元が要る問いは 2 つだけである ——
// 「これらの利用者名は実在するか」（DataSourceService）と
// 「この 1 人の属性は何か」（McpServer の登録者自身）。
//
// 🔴 **「居ない」と「引けなかった」を型で分ける。**
//   居ない = 応答（`exists=false` / `Found=false`）、引けなかった = 戻り値 `null`。
//   呼び出し元は**後者だけ**を自分の「Unavailable」へ倒す —— 混ぜると認可サービスの障害が
//   「その利用者は存在しません」という嘘の理由になる。
//   `RpcException`（全 status。`UNAUTHENTICATED` / `PERMISSION_DENIED` / `UNAVAILABLE`）と
//   s2s トークン取得失敗（`InvalidOperationException`）は**どちらも** `null` である。
public sealed class UserDirectoryGrpcClient(
    Pb.UserDirectory.UserDirectoryClient client,
    ILogger<UserDirectoryGrpcClient> logger)
{
    /// <summary>
    /// FR-05, UC-04, SC-06, ADR-0074 決定 4: 送った利用者名のうち**実在するもの**を返す。
    /// 引けなかったときは <c>null</c>（空集合ではない）。
    /// 🔴 照合は呼び出し先が**序数一致**で行う（DataSourceService の現行と同じ）。
    /// 🔴 無効化された利用者も実在として数える（同 決定 4 が課すのは「実在」であって「有効」ではない）。
    /// </summary>
    public async Task<IReadOnlySet<string>?> CheckUsernamesAsync(
        IReadOnlyCollection<string> usernames, CancellationToken ct)
    {
        var request = new Pb.CheckUsernamesRequest();
        request.Usernames.AddRange(usernames);

        try
        {
            var resp = await client.CheckUsernamesAsync(request, cancellationToken: ct);
            return new HashSet<string>(
                resp.Results.Where(r => r.Exists).Select(r => r.Username), StringComparer.Ordinal);
        }
        catch (RpcException ex)
        {
            logger.LogWarning(
                "利用者名簿の gRPC 照会に失敗しました（{Status}）。写像先の実在検証は行えません。", ex.StatusCode);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "s2s トークンが取得できないため利用者名簿を照会できません。");
            return null;
        }
    }

    /// <summary>
    /// FR-16, UC-09, SC-12, ADR-0062 決定 3: 名指しした 1 人の ABAC 属性。
    /// 引けなかったときは <c>null</c>、名簿に居なければ <see cref="PlatformUserAttributes.Found"/> が false。
    /// 🔴 照合は呼び出し先が**大小文字無視**で行う（McpServer の現行と同じ）。
    /// 🔴 属性は REST と同じ線上表現（集合値キーはカンマ連結。IADR-0385 決定 2）。
    /// </summary>
    public async Task<PlatformUserAttributes?> GetUserAttributesAsync(string username, CancellationToken ct)
    {
        try
        {
            var resp = await client.GetUserAttributesAsync(
                new Pb.GetUserAttributesRequest { Username = username }, cancellationToken: ct);
            return new PlatformUserAttributes(
                resp.Found, resp.Username, new Dictionary<string, string>(resp.Attributes));
        }
        catch (RpcException ex)
        {
            logger.LogWarning(
                "登録者の ABAC 属性の gRPC 解決に失敗しました（{Status}）。属性は検証できません。", ex.StatusCode);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "s2s トークンが取得できないため登録者の ABAC 属性を解決できません。");
            return null;
        }
    }
}

// FR-16, SC-12, IADR-0385 決定 2, IADR-0401 (#1255): 名簿から読んだ 1 人の像。
// **ロール・有効状態・内部 ID は運ばない** —— 呼び出し元が使わないものを面へ出さない。
public sealed record PlatformUserAttributes(
    bool Found, string Username, IReadOnlyDictionary<string, string> Attributes);

public static class UserDirectoryGrpcClientExtensions
{
    // `Services:AuthorizationServiceGrpc`（`AuthzScopeGrpcClient.AddressKey` と**同じキー**）が
    // 構成されたときだけ登録する。未設定なら何も登録せず、呼び出し元は REST のまま。
    //
    // チャネルは `AddAuthzScopeGrpcClient` と**共有する**（宛先が同じ 1 つの認可サービスであり、
    // HTTP/2 は多重化される。`AuthzScopeGrpcClientExtensions.AddChannel` の注記を参照）。
    public static IServiceCollection AddUserDirectoryGrpcClient(
        this IServiceCollection services, IConfiguration config)
    {
        var address = config[AuthzScopeGrpcClient.AddressKey];
        if (string.IsNullOrWhiteSpace(address))
            return services;

        services.AddPlatformServiceToken(config);
        AuthzScopeGrpcClientExtensions.AddChannel(services, address);
        services.AddSingleton(sp =>
            new Pb.UserDirectory.UserDirectoryClient(sp.GetRequiredService<global::Grpc.Net.Client.GrpcChannel>()));
        services.AddSingleton<UserDirectoryGrpcClient>();
        return services;
    }
}
