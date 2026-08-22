using System.Security.Claims;
using McpServer.Api.Foundation.Domain;
using McpServer.Api.Foundation.Persistence;
using Microsoft.EntityFrameworkCore;

namespace McpServer.Api.Foundation.Services;

// FR-16, UC-08 事前条件・例外フロー, UC-09: トークンの主体を、登録済みクライアントと突合して解決する。
//
// 🔴 **主体種別（有人 / サービスアカウント）はトークンではなく登録簿から採る。** トークンの
// クレームを信じると、クライアント側の申告で ADR-0034 決定 9 の適用対象から外れられてしまう。
// 登録簿は管理者だけが変更できる（UC-09・SC-12 は管理者限定）。
//
// 🔴 **毎回問い合わせる（キャッシュしない）。** UC-09 の「無効化したクライアントは接続・実行を
// 拒否する」は**即時**でなければならない。キャッシュを挟むと無効化が TTL 分だけ効かない。
public sealed class McpSubjectResolver(McpDbContext db)
{
    // Keycloak が発行するクライアント識別のクレーム候補。`azp`（authorized party）を第一とする。
    private static readonly string[] ClientIdClaims = ["azp", "client_id", "clientId"];

    public async Task<(McpSubject? Subject, McpClient? Client, string? Error)> ResolveAsync(
        ClaimsPrincipal principal, CancellationToken ct)
    {
        var clientId = ClientIdClaims
            .Select(principal.FindFirstValue)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        if (string.IsNullOrWhiteSpace(clientId))
            return (null, null, "MCP クライアントを特定できません。");

        var client = await db.Clients.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ClientId == clientId, ct);

        // UC-09 例外フロー: 未登録クライアント・無効化済みクライアントは実行を拒否する。
        // **両者を同じ文言で返す**（登録の有無を外部エージェントへ漏らさない）。
        if (client is null || !client.Enabled)
            return (null, null, "MCP クライアントが登録されていないか、無効化されています。");

        // 有人エージェントは利用者の代理であり、主体は利用者本人である。
        // サービスアカウントには自然人の主体が無いため、クライアント ID をそのまま主体とする
        // （ADR-0034 決定 9 の「本人がいない」という性質そのものである）。
        var subjectId = client.Kind == McpClientKind.ServiceAccount
            ? client.ClientId
            : principal.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? principal.Identity?.Name
                ?? client.ClientId;

        return (new McpSubject(subjectId, client.ClientId, client.Kind, client.Attributes), client, null);
    }
}
