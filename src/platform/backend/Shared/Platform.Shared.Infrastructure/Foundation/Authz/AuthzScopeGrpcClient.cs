using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Dto = Platform.Shared.Contracts.Dtos;
using Pb = Platform.Shared.Contracts.Grpc.Authz.V1;

namespace Platform.Shared.Infrastructure.Foundation.Authz;

// FR-05, NFR-09, ADR-0029, ADR-0075, IADR-0379 (#1201): 認可スコープ解決の **gRPC 経路**（参照実装）。
// REST 経路（BffScopeResolver の HTTP）と同じ deny-by-default で縮退する ——
// UNAUTHENTICATED / PERMISSION_DENIED / UNAVAILABLE / s2s トークン取得失敗 のいずれも null（閲覧可能なし）。
// ［2026-09-27 / #1646］呼び出し元の取り消しだけは null にせず、呼び出し元の token を持つ
// `OperationCanceledException` で外へ出す（REST 経路の `BffScopeResolver` と同じ。取り消しを deny と記録しない）。
//
// ［2026-10-10 / #1255・[[IADR-0533]]］**REST の並走は撤去した。認可スコープ解決の輸送は本クライアントだけである**
// （[[IADR-0379]] 決定 5「並走中の正は REST」を反転した）。宛先 `Services:AuthorizationServiceGrpc` が構成されていなければ
// 常に `UNAVAILABLE` を返す呼び出し器の上に組まれ（`UnconfiguredGrpcDestination`）、上の deny-by-default へ落ちる。
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
        // 🔴 #1646: **呼び出し元の取り消しは `null` へ畳まない。** 本物のチャネルは取り消しを `RpcException(Cancelled)` で
        // 投げる（`ThrowOperationCanceledOnCancellation` は既定の false）ので、縮退の catch より前で拾い、呼び出し元の
        // token を持つ `OperationCanceledException` へ揃える。判定は status ではなく呼び出し元の ct で行う
        // （受け口が返した `CANCELLED`・期限切れは従来どおり `null`）。
        catch (Exception) when (ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
            throw;
        }
        catch (RpcException ex)
        {
            // 認可サービス不調・資格情報不備は deny-by-default（null）へ縮退する。
            logger.LogWarning(
                "認可スコープの gRPC 解決に失敗しました（{Status}: {Detail}）。閲覧可能なしへ縮退します。",
                ex.StatusCode, ex.Status.Detail);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            // s2s トークンが取れない（構成不備・IdP 不達）。匿名では呼ばず deny へ倒す。
            logger.LogWarning(ex, "s2s トークンが取得できないため認可スコープを解決できません。閲覧可能なしへ縮退します。");
            return null;
        }
    }

    // FR-05, FR-13, FR-16, FR-17, UC-01, UC-07, UC-09, UC-10, ADR-0034, ADR-0036, IADR-0272 決定 4,
    // IADR-0401 決定 1 (#1255): 契約 DTO（`AccessScopeResponse`）を返す多重定義。
    //
    // **上の `ResolveAsync` は BFF 専用の資料型（`BffAccessScope?`）を返すため、後段サービス
    // （AiAnalysis / Graph / Wiki / McpServer）はそのままでは使えない。** 4 者はいずれも REST の
    // `POST /authz/scope` の応答（`AccessScopeResponse`）をそのまま扱っており、**移行の不変条件は
    // 「本文を変えない輸送の差し替え」**である。したがって写像だけを足し、既存の口は 1 文字も変えない。
    //
    // 🔴 **縮退は 4 者の現行と同じ deny-by-default**（`Granted=false`）である ——
    // `RpcException`（全 status）と s2s トークン取得失敗はすべて `new AccessScopeResponse(userId, [], false)`
    // へ落ちる。REST 実装が非 2xx・不達を同じ形へ落としているのと**同じ枝**であり、
    // 「引けなかった」を「見えるものが無い」へ倒す向き（安全側）も変わらない。
    //
    // 🔴 **`action` に既定値を置かない**（IADR-0272 決定 4）。既定を残すと、新しい経路を足した人が
    // 書き忘れることで認可が緩む。呼び出し元が明示する。
    public async Task<Dto.AccessScopeResponse> ResolveScopeAsync(
        string userId, IReadOnlyDictionary<string, string> userAttributes, string action, CancellationToken ct)
        => await TryResolveScopeAsync(userId, userAttributes, action, ct)
           ?? new Dto.AccessScopeResponse(userId, [], false);

    // FR-05, FR-16, UC-09, SC-12, ADR-0062 決定 3, IADR-0384, IADR-0401 決定 5 (#1255):
    // 🔴 **「引けなかった」を `null` で返す**多重定義。
    //
    // 上の `ResolveScopeAsync` は輸送の失敗を deny（`Granted=false`）へ畳む —— AiAnalysis / Graph /
    // Wiki の REST 実装がそうしているからである。**しかし McpServer は畳めない。**
    // あちらは「読めるものが無い（配れるものも無い）」と「引けなかった（判定できない）」で
    // **返す型が違う**（`RegistrarAssignableAttributes.Of` と `.Unavailable`）。畳むと、
    // 認可サービスが落ちている間に **`clearance` は空だがタグは配れる**という、
    // REST 実装には無い**緩む向き**の挙動になる。
    //
    // したがって縮退の畳み方は**呼び出し元が決める**。ここは事実（引けたか）だけを返す。
    public async Task<Dto.AccessScopeResponse?> TryResolveScopeAsync(
        string userId, IReadOnlyDictionary<string, string> userAttributes, string action, CancellationToken ct)
    {
        var request = new Pb.ResolveScopeRequest { UserId = userId, Action = action };
        foreach (var (key, value) in userAttributes)
            request.UserAttributes[key] = value;

        try
        {
            var resp = await client.ResolveAsync(request, cancellationToken: ct);
            return new Dto.AccessScopeResponse(
                resp.UserId,
                resp.AllowedFilters.Select(ToFilter).ToList(),
                resp.Granted,
                resp.Branches.Count == 0
                    ? null
                    : resp.Branches
                        .Select(b => new Dto.AccessScopeBranch(b.Name, b.Filters.Select(ToFilter).ToList()))
                        .ToList());
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // #1646: 呼び出し元の取り消しは `null` へ畳まない（上の `ResolveAsync` の注記を参照）。
            ct.ThrowIfCancellationRequested();
            throw;
        }
        catch (RpcException ex)
        {
            logger.LogWarning(
                "認可スコープの gRPC 解決に失敗しました（{Status}: {Detail}）。「引けなかった」として返します。",
                ex.StatusCode, ex.Status.Detail);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "s2s トークンが取得できないため認可スコープを解決できません。");
            return null;
        }
    }

    private static Dto.AttributeFilter ToFilter(Pb.AttributeFilter f) => new(f.Key, f.AllowedValues.ToList());
}

public static class AuthzScopeGrpcClientExtensions
{
    // `Services:AuthorizationServiceGrpc`（h2c のアドレス。例: http://authorization-service:8081）の生成クライアントを登録する。
    // ［2026-10-10 / [[IADR-0533]] 決定 2］**常に登録する。** 宛先が構成されていなければ、生成クライアントを
    // 常に `UNAVAILABLE` を返す呼び出し器（`UnconfiguredGrpcDestination`）の上に組む —— 呼び出し元は
    // 「認可サービスへ届かない」と同じ deny-by-default へ落ちる（REST へ戻る経路はもう無い）。
    public static IServiceCollection AddAuthzScopeGrpcClient(this IServiceCollection services, IConfiguration config)
    {
        var address = config[AuthzScopeGrpcClient.AddressKey];
        if (!string.IsNullOrWhiteSpace(address))
        {
            services.AddPlatformServiceToken(config);
            AddChannel(services, address);
            services.TryAddSingleton(sp =>
                new Pb.AuthzScope.AuthzScopeClient(sp.GetRequiredService<global::Grpc.Net.Client.GrpcChannel>()));
        }

        services.TryAddUnconfiguredGrpcClient(AuthzScopeGrpcClient.AddressKey, ci => new Pb.AuthzScope.AuthzScopeClient(ci));
        services.TryAddSingleton<AuthzScopeGrpcClient>();
        return services;
    }

    // IADR-0401 (#1255): 認可サービス宛のチャネルは `AddAuthzScopeGrpcClient` と
    // `AddUserDirectoryGrpcClient` の**両方から**登録され得る（McpServer は 2 つとも要る）。
    // 宛先は同じ 1 つであり HTTP/2 は多重化されるので、**チャネルは 1 本に保つ**
    // （2 本張ると接続が二重になる）。`TryAddSingleton` はキー無しの `GrpcChannel` を
    // 先着 1 つに固定する —— LlmGateway 側は**キー付き**で登録されるので衝突しない
    // （`LlmGatewayGrpcClientExtensions.ChannelKey`）。
    internal static void AddChannel(IServiceCollection services, string address) =>
        services.TryAddSingleton(sp =>
            GrpcClientExtensions.CreatePlatformChannel(address, sp.GetRequiredService<IServiceTokenProvider>()));
}
