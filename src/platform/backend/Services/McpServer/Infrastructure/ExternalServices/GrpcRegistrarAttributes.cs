using McpServer.Domain;
using McpServer.Domain.Ports;
using Platform.Shared.Infrastructure.Foundation.Authz;

namespace McpServer.Infrastructure.ExternalServices;

// FR-16, FR-05, UC-09, SC-12, NFR-09, NFR-16, ADR-0004, ADR-0029, ADR-0036 D-01・D-02,
// ADR-0062 決定 2・3, ADR-0075, [[IADR-0329]], [[IADR-0379]], [[IADR-0384]], [[IADR-0385]],
// [[IADR-0401]] 決定 2・4 (#1255):
// 登録者の割当可能属性の **east-west gRPC 実装**（兄弟クラス。REST 実装は
// `AuthorizationServiceRegistrarAttributes`）。
//
// ■ 2 本の問い合わせは REST と同じ（目的が違う 2 つ）
//   1. `UserDirectory/GetUserAttributes(username)` … 登録者自身の ABAC 属性（タグ）。
//      REST の `GET /authz/users`（全件列挙 → 呼び出し元で 1 行に絞る）を**狭い問いへ置き換えた**もの。
//   2. `AuthzScope/Resolve(action=read)` … 登録者が読める機密区分の集合。**REST と同じ rpc・同じ本文**。
//
// 🔴 **利用者の `Authorization` を転送しない**（[[IADR-0379]] 決定 4）。転送すると呼び出し先が
// 「利用者が直接呼んだ」と区別できず confused deputy になる。代わりに読み口を狭めた
// （[[IADR-0401]] 決定 2）—— 列挙も書き込みも s2s の面に無いので、SC-12 を触れない主体が
// **名簿を引ける経路はできない**。人の側の門（`/mcp-clients` の `AdminOnly`）は端点に残る。
//
// 🔴 **登録者は自分自身の名前でしか引かない。** 引く名前は `HttpContext.User.Identity.Name`
// （`preferred_username`。判定側が読むのと同じ主体識別子）であり、**要求本文から取らない** ——
// 取ると「他人の属性で登録できる」経路になる。`GrpcRegistrarAttributesTests` が
// 偽クライアントの受け取った `username` を突き合わせて固定する。
//
// 🔴 **`ReadAssignableConfidentiality` は REST 実装と同じ 1 つを呼ぶ**
// （`RegistrarScopeReading`。[[IADR-0384]] 決定 1）—— 写しを 2 つ作ると片方だけが
// fail-open へ戻る事故（#1242）が輸送ごとに再現し得る。
//
// ■ 縮退（REST 実装と同じ枝）
//   `RpcException`（全 status）・s2s トークン取得失敗・**登録者を名簿に見つけられない**は
//   いずれも `Unavailable`（＝引けなかった）。**「1 つも持っていない」と混ぜない。**
//   「名簿に居ない」を Unavailable へ倒すのは REST 実装の判断をそのまま写したものである
//   （名簿と主体識別子の食い違いが属性の不足として沈黙しない）。
public sealed class GrpcRegistrarAttributes(
    UserDirectoryGrpcClient directory,
    AuthzScopeGrpcClient scopes,
    IHttpContextAccessor httpContextAccessor,
    ILogger<GrpcRegistrarAttributes> logger) : IRegistrarAttributeResolver
{
    // FR-05, ADR-0036 D-07, [[IADR-0272]] 決定 4: 読み取り経路の解決である（REST 実装と同じ "read"）。
    private const string ScopeAction = "read";

    public async Task<RegistrarAssignableAttributes> ResolveAsync(CancellationToken ct)
    {
        // 判定側が読むのと同じ主体識別子（`preferred_username`。AuthExtensions.NameClaimType）。
        var username = httpContextAccessor.HttpContext?.User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(username))
        {
            logger.LogWarning("登録者を特定できません（主体の識別子が空）。無人アカウントの属性は検証できません。");
            return RegistrarAssignableAttributes.Unavailable;
        }

        var registrar = await directory.GetUserAttributesAsync(username, ct);
        // null = 引けなかった（輸送・資格情報）。Found=false = 名簿に居ない。
        // 🔴 **どちらも Unavailable へ倒す**（REST 実装の判断と同じ。上の注記を参照）。
        if (registrar is null || !registrar.Found)
        {
            if (registrar is { Found: false })
                logger.LogWarning("登録者を利用者名簿に見つけられませんでした。無人アカウントの属性は検証できません。");
            return RegistrarAssignableAttributes.Unavailable;
        }

        // 🔴 **`TryResolveScopeAsync` を使う**（`ResolveScopeAsync` ではない。[[IADR-0401]] 決定 5）。
        // 後者は輸送の失敗を `Granted=false` へ畳むが、本サービスは「読めるものが無い」と
        // 「引けなかった」で**返す型が違う** —— 畳むと認可サービスの障害中に
        // **`clearance` は空だがタグは配れる**という、REST 実装には無い**緩む向き**の挙動になる。
        var scope = await scopes.TryResolveScopeAsync(
            registrar.Username, registrar.Attributes, ScopeAction, ct);
        if (scope is null)
        {
            logger.LogWarning("登録者の認可スコープを解決できませんでした。無人アカウントの属性は検証できません。");
            return RegistrarAssignableAttributes.Unavailable;
        }

        var (unrestricted, clearance) = RegistrarScopeReading.ReadAssignableConfidentiality(scope);

        var tags = registrar.Attributes.TryGetValue(ServiceAccountAttributeSubset.TagsKey, out var raw)
            ? ServiceAccountAttributeSubset.Tokens(raw)
            : ServiceAccountAttributeSubset.Tokens(null);

        return RegistrarAssignableAttributes.Of(clearance, tags, unrestricted);
    }
}
