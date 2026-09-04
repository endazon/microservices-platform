using System.Net.Http.Json;
using McpServer.Domain;
using McpServer.Domain.Ports;
using Platform.Shared.Contracts.Dtos;

namespace McpServer.Infrastructure.ExternalServices;

// FR-16, FR-05, UC-09, SC-12, ADR-0062 決定 3, ADR-0004: 登録者が無人アカウントへ渡してよい
// 属性値の集合を、AuthorizationService から解決する。
//
// ■ 経路（2 本の問い合わせ。目的が違う）
//   1. `GET /authz/users` … 登録者の ABAC 属性そのもの（タグ）。後段は
//      `IIdentityAdminClient` → Keycloak Admin API（`view-users` を持つ機密クライアント。IADR-0329）。
//      **本サービスは Keycloak を直接叩かない**（叩くと `view-users` を持つ主体が 2 つになる）。
//      DataSourceService の `AuthorizationServiceUserDirectory` と同型である。
//   2. `POST /authz/scope`（action=read） … 登録者が読める**機密区分の集合**。
//
// ■ 🔴 なぜ `clearance` の集合を評価器から引くのか
//   計画 07_abac-attribute-model は序数比較を意図的に排除しており、`clearance` の階段は
//   **ポリシー（各段の許可集合の明示列挙）にしか存在しない**。後段が階段表を持つと、
//   計画が退けた序数をコードへ再導入することになる。**評価器に聞く。**
//   `clearance` と `confidentiality` は属性辞書上**同一の値域**であり（07_abac-attribute-model
//   の文書基本属性／利用者属性）、「読める機密区分の集合」がそのまま「渡してよい `clearance` の
//   集合」になる。
//
// ■ 🔴 `Branches` ではなく `AllowedFilters`（キー単位の union）から読む
//   所有者ベースの分岐は `confidentiality` の条件を持たない。これを「全値許可」と読むと、
//   **登録者が自分の文書を読めることを根拠に `restricted` を配れてしまう** ——
//   サービスアカウントは登録者の所有権を継がないため、その読みは緩む向きに誤っている。
//   キー単位 union は当該キーの**属性ベースの到達範囲**を表し、条件を持たない分岐はこのキーの
//   union を広げない（＝安全側）。IADR-0253 決定 2 が警告する混成は**複数キーの連言を 1 本へ潰す**
//   話であり、単一キーの許可値の読み出しには当たらない。
//
// ■ 🔴 呼び出し元の `Authorization` を転送する
//   `/authz/users` は AdminOnly であり、SC-12 も AdminOnly なので**呼び出し元の資格情報が
//   そのまま通る**。サービス専用の資格情報を新設しない（新設すると SC-12 を触れない主体が
//   名簿を引ける経路ができる）。
//
// ■ 縮退
//   不達・非 2xx・登録者を名簿に見つけられない、はいずれも `Unavailable`（＝引けなかった）。
//   **「1 つも持っていない」と混ぜない。**
public sealed class AuthorizationServiceRegistrarAttributes(
    IHttpClientFactory httpFactory,
    IHttpContextAccessor httpContextAccessor,
    ILogger<AuthorizationServiceRegistrarAttributes> logger) : IRegistrarAttributeResolver
{
    public const string HttpClientName = "AuthorizationService";

    // 文書側の機密区分キー。`clearance`（主体側）と同一の値域を持つ（07_abac-attribute-model）。
    private const string ConfidentialityKey = "confidentiality";

    public async Task<RegistrarAssignableAttributes> ResolveAsync(CancellationToken ct)
    {
        var http = httpContextAccessor.HttpContext;
        // 判定側が読むのと同じ主体識別子（`preferred_username`。AuthExtensions.NameClaimType）。
        var username = http?.User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(username))
        {
            logger.LogWarning("登録者を特定できません（主体の識別子が空）。無人アカウントの属性は検証できません。");
            return RegistrarAssignableAttributes.Unavailable;
        }

        var client = httpFactory.CreateClient(HttpClientName);
        var auth = http?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);

        try
        {
            var registrar = await FindRegistrarAsync(client, username, ct);
            if (registrar is null) return RegistrarAssignableAttributes.Unavailable;

            var (unrestricted, clearance) = await ResolveClearanceAsync(client, registrar, ct);
            if (clearance is null) return RegistrarAssignableAttributes.Unavailable;

            var tags = registrar.Attributes.TryGetValue(ServiceAccountAttributeSubset.TagsKey, out var raw)
                ? ServiceAccountAttributeSubset.Tokens(raw)
                : ServiceAccountAttributeSubset.Tokens(null);

            return RegistrarAssignableAttributes.Of(clearance, tags, unrestricted);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "登録者の ABAC 属性を解決できませんでした（{ErrorType}）。無人アカウントの属性は検証できません。",
                ex.GetType().FullName);
            return RegistrarAssignableAttributes.Unavailable;
        }
    }

    // **`Username` で突き合わせる。** 判定側が主体として読むのは `preferred_username` であり、
    // 内部 ID（UUID）ではない（`AuthorizationServiceUserDirectory` と同じ理由）。
    private async Task<PlatformUserDto?> FindRegistrarAsync(
        HttpClient client, string username, CancellationToken ct)
    {
        var resp = await client.GetAsync("/authz/users", ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "利用者名簿の取得に失敗しました（HTTP {Status}）。無人アカウントの属性は検証できません。",
                (int)resp.StatusCode);
            return null;
        }

        var users = await resp.Content.ReadFromJsonAsync<List<PlatformUserDto>>(ct);
        var registrar = users?.FirstOrDefault(
            u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));

        if (registrar is null)
        {
            // **「名簿に居ない」も引けなかった側へ倒す。** 配らない点では同じだが、ここで
            // 「あなたは何も持っていません」と断定すると、名簿と主体識別子の食い違いが
            // 属性の不足として沈黙する。
            logger.LogWarning("登録者を利用者名簿に見つけられませんでした。無人アカウントの属性は検証できません。");
        }
        return registrar;
    }

    // 戻り値の集合が null なら「引けなかった」。空集合は「読める機密区分が無い」（deny-by-default）。
    private async Task<(bool Unrestricted, IReadOnlyList<string>? Clearance)> ResolveClearanceAsync(
        HttpClient client, PlatformUserDto registrar, CancellationToken ct)
    {
        var resp = await client.PostAsJsonAsync(
            "/authz/scope",
            new AccessScopeRequest(registrar.Username, registrar.Attributes, "read"),
            ct);

        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "登録者の認可スコープを解決できませんでした（HTTP {Status}）。無人アカウントの属性は検証できません。",
                (int)resp.StatusCode);
            return (false, null);
        }

        var scope = await resp.Content.ReadFromJsonAsync<AccessScopeResponse>(ct);
        if (scope is null) return (false, null);

        // 許可ポリシーが 1 つも無い＝読めるものが無い。**配れるものも無い**（引けなかったのではない）。
        if (!scope.Granted) return (false, []);

        var filter = scope.AllowedFilters
            .FirstOrDefault(f => string.Equals(f.Key, ConfidentialityKey, StringComparison.OrdinalIgnoreCase));

        // 契約上「Granted かつフィルタ無し ＝ 条件無しで許可（全件可）」。機密区分で絞る根拠が無い。
        return filter is null ? (true, []) : (false, filter.AllowedValues);
    }
}
