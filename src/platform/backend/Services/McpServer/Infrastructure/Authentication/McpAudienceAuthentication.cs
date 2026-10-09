using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace McpServer.Infrastructure.Authentication;

// FR-16, UC-08, 計画 ADR-0134 決定 1・フォローアップ 2, [[IADR-0516]]（2026-10-09 追記 / #1844）:
// **MCP のプロトコル面（`/mcp`）はトークンの audience を検証する。** audience は MCP サーバー（`mcp-server`）に限る。
//
// ■ 🔴 **既定のスキームは変えない**（`AddPlatformAuth` の `ValidateAudience = false` のまま）。管理 API（`/mcp-clients`）は BFF が
//   **利用者のトークン**（aud は MCP サーバーではない）を中継して呼ぶ（`McpClientBffEndpoints.Proxy`）。既定のスキームで audience を
//   検証すると SC-12 の管理が 401 で閉じる。全サービスの共通設定（`AuthExtensions`）の是正は別 issue（#1846）。
// ■ そのため `/mcp` にだけ、**別の JWT スキーム**（<see cref="Scheme"/>）とポリシー（<see cref="Policy"/>）を掛ける。
//   スキームの発行元・メタデータの取得先・署名鍵の取り方・名前とロールのクレームは**既定のスキームと同じ 1 つの設定**
//   （`AuthExtensions.PlatformJwtBearer`）を当てる（発行元の検証を 2 つにしない。IADR-0086 の分離もそのまま効く）。
//   変えるのは `ValidateAudience=true`・`ValidAudience` だけである。
// ■ ポリシーが `AuthenticationSchemes` を持つので、`/mcp` の要求では既定のスキームで得た主体は使われない（認可の段で
//   <see cref="Scheme"/> で認証し直し、失敗すれば 401）。
// ■ audience をトークンへ載せるのは、SC-12 が作るクライアントの audience の写像（`oidc-audience-mapper`・
//   `included.custom.audience=mcp-server`）である（`KeycloakServiceAccountProvisioner` の 2 つのテンプレート）。
public static class McpAudienceAuthentication
{
    /// <summary>MCP サーバーの audience（トークンの <c>aud</c>）。SC-12 が作るクライアントの写像と同じ 1 つの値。</summary>
    public const string Audience = "mcp-server";

    /// <summary><c>/mcp</c> の JWT スキーム名。</summary>
    public const string Scheme = "McpAudience";

    /// <summary><c>/mcp</c> に掛けるポリシー名。</summary>
    public const string Policy = "McpAudience";

    public static IServiceCollection AddMcpAudienceAuthentication(this IServiceCollection services, IConfiguration config)
    {
        // 既定のスキームと**同じ 1 つ**（`AuthExtensions.PlatformJwtBearer`。登録の時点の構成を読む）を当ててから、audience だけを変える。
        var platform = AuthExtensions.PlatformJwtBearer(config);
        services.AddAuthentication().AddJwtBearer(Scheme, options =>
        {
            platform(options);
            options.TokenValidationParameters.ValidateAudience = true;
            options.TokenValidationParameters.ValidAudience = Audience;
        });
        services.AddAuthorizationBuilder()
            .AddPolicy(Policy, policy => policy
                .AddAuthenticationSchemes(Scheme)
                .RequireAuthenticatedUser());
        return services;
    }
}
