using System.Net.Http.Json;
using DataSourceService.Domain.Ports;
using Platform.Shared.Contracts.Dtos;

namespace DataSourceService.Infrastructure.ExternalServices;

// FR-05, UC-04, SC-06, SC-17, ADR-0064 決定 4, ADR-0074 決定 4 (#1194):
// AuthorizationService の `GET /authz/users` を叩いて基盤の利用者名簿を得る（**REST 実装**）。
//
// ■ 経路
//   DataSourceService →（HTTP）→ AuthorizationService →（`IIdentityAdminClient`）→ Keycloak Admin API。
//   最後の段が `view-users` を持つ機密クライアント `identity-admin` である（IADR-0329 決定 1）。
//   **本サービスは Keycloak を直接叩かない** —— 叩くと `view-users` を持つ主体が 2 つになり、
//   ADR-0064 決定 4 が分けた線が消える。
//
// ■ 🔴 **呼び出し元の `Authorization` を転送する**
//   `/authz/users` は AdminOnly である。SC-06 の登録・更新も管理者限定なので、
//   **呼び出し元の資格情報がそのまま通る**（サービス専用の資格情報を新設しない ——
//   新設すると、SC-06 を触れない主体が名簿を引ける経路ができる）。
//   BFF セッション方式では `SessionTokenPropagationMiddleware` が Bearer を立て、
//   `DataSourceBffEndpoints.CreateForwardingClient` が後段へ引き継いでいる（ADR-0032 / IADR-0251）。
//
//   ［2026-09-06 追記 / #1255・[[IADR-0401]] 決定 2］🔴 **この転送は REST 実装だけの性質である。**
//   east-west gRPC の面へは利用者トークンを載せない（[[IADR-0379]] 決定 4 /
//   `docs/api/east-west-grpc.md` §4）。gRPC 実装（`GrpcPlatformUserDirectory`）は転送せず、
//   **呼び出し先の読み口を「これらの名前は実在するか」に狭めること**で同じ答えを得る ——
//   上の「新設すると SC-06 を触れない主体が名簿を引ける経路ができる」という懸念は、
//   **列挙を s2s の面へ出さない**ことで満たされる（名簿は引けない）。
//   人の側の門（Create / Update / Patch / Disable の `AdminOnly`）は本サービスの端点に残る。
//
// ■ 縮退
//   非 2xx・不達はいずれも `Unavailable`（＝「引けなかった」）へ倒す。**空集合と混ぜない** ——
//   混ぜると認可サービスの障害が「その利用者は存在しません」という嘘の理由になる。
public sealed class AuthorizationServiceUserDirectory(
    IHttpClientFactory httpFactory,
    IHttpContextAccessor httpContextAccessor,
    ILogger<AuthorizationServiceUserDirectory> logger) : IPlatformUserDirectory
{
    public const string HttpClientName = "AuthorizationService";

    // [[IADR-0401]] 決定 3 (#1255): 口が「照会」になったので、**列挙してから交差する**。
    // 🔴 **挙動は 1 バイトも変わらない** —— 呼び出し元（`OwnerMappingValidation`）は
    // 従前も全件の名簿に対して `Contains` を引いており、交差を先に取っても答えは同じである。
    public async Task<PlatformUserDirectorySnapshot> LookupAsync(
        IReadOnlySet<string> usernames, CancellationToken ct)
    {
        var client = httpFactory.CreateClient(HttpClientName);

        var auth = httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);

        try
        {
            var resp = await client.GetAsync("/authz/users", ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "利用者名簿の取得に失敗しました（HTTP {Status}）。写像先の実在検証は行えません。",
                    (int)resp.StatusCode);
                return PlatformUserDirectorySnapshot.Unavailable;
            }

            var users = await resp.Content.ReadFromJsonAsync<List<PlatformUserDto>>(ct);
            if (users is null) return PlatformUserDirectorySnapshot.Unavailable;

            // **`Username` を採る。** `owner` として突き合わされるのは `preferred_username` であり
            // （`AuthExtensions.NameClaimType` / `DocumentBodyIntake.CanWrite`）、
            // 内部 ID（UUID）ではない。ここを Id にすると保存も検証も通るのに 1 度も一致しない。
            //
            // **無効化された利用者も名簿に含める。** ADR-0074 決定 4 が課すのは「実在すること」で
            // あり、有効であることではない（退職者が所有者だった文書は所有者を失わない）。
            //
            // 🔴 **交差は序数一致である**（`usernames` は呼び出し元が `StringComparer.Ordinal` で
            // 組んだ集合。`PlatformUserDirectorySnapshot.Of` も同じ比較子で畳む）。
            return PlatformUserDirectorySnapshot.Of(
                users.Select(u => u.Username)
                    .Where(u => !string.IsNullOrWhiteSpace(u) && usernames.Contains(u)));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "利用者名簿の取得に失敗しました（{ErrorType}）。写像先の実在検証は行えません。",
                ex.GetType().FullName);
            return PlatformUserDirectorySnapshot.Unavailable;
        }
    }
}
