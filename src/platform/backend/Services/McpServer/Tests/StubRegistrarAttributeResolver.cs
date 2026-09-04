using McpServer.Domain;
using McpServer.Domain.Ports;
using Microsoft.AspNetCore.Http;

namespace McpServer.Tests;

// FR-16, SC-12, ADR-0062: 登録者が配れる属性値の集合を**要求ヘッダから注入する**スタブ。
//
// 本物は AuthorizationService の `/authz/users` ＋ `/authz/scope` を叩く。ここで検査したいのは
// **後段の判定**であり、認可サービスの疎通ではない（疎通は稼働クラスタでの実測で確かめる）。
// `TestAuthHandler` がロールをヘッダで差し替えるのと同じ作法に揃えてある。
//
// 🔴 **既定は「引けなかった」である。** deny-by-default をスタブ側の既定でも表す ——
// ヘッダを書き忘れたテストが「たまたま通る」形にしない。
public sealed class StubRegistrarAttributeResolver(IHttpContextAccessor accessor) : IRegistrarAttributeResolver
{
    /// <summary>登録者が配れる機密区分（カンマ区切り）。</summary>
    public const string ClearanceHeader = "X-Test-Registrar-Clearance";

    /// <summary>登録者が持つタグ（カンマ区切り）。</summary>
    public const string TagsHeader = "X-Test-Registrar-Tags";

    /// <summary>機密区分で絞られていない（契約の「条件無しで許可」）。</summary>
    public const string UnrestrictedHeader = "X-Test-Registrar-Clearance-Unrestricted";

    public Task<RegistrarAssignableAttributes> ResolveAsync(CancellationToken ct)
    {
        var headers = accessor.HttpContext?.Request.Headers;
        if (headers is null) return Task.FromResult(RegistrarAssignableAttributes.Unavailable);

        var unrestricted = headers.TryGetValue(UnrestrictedHeader, out var u)
            && string.Equals(u.ToString(), "true", StringComparison.OrdinalIgnoreCase);

        var hasClearance = headers.TryGetValue(ClearanceHeader, out var clearance);
        var hasTags = headers.TryGetValue(TagsHeader, out var tags);
        if (!hasClearance && !hasTags && !unrestricted)
            return Task.FromResult(RegistrarAssignableAttributes.Unavailable);

        return Task.FromResult(RegistrarAssignableAttributes.Of(
            ServiceAccountAttributeSubset.Tokens(clearance.ToString()),
            ServiceAccountAttributeSubset.Tokens(tags.ToString()),
            unrestricted));
    }
}
