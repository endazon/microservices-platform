using System.Security.Claims;
using System.Text.Json;

namespace Platform.Shared.Infrastructure.Foundation.Audit;

// FR-09, FR-17, SC-09, ADR-0004, NFR-28, [[IADR-0647]] (#2124): 管理画面の書き込み操作（透過中継）の監査を 1 行で残す。
//
// SC-09 の書き込みは BFF の 3 群（`/bff/admin/authz`・`/bff/tags`・`/bff/edge-types`）を通る。platform と knowledge の
// 両方から使うので platform の Shared に置く（knowledge が参照してよいのは platform の Shared だけ）。
// ■ 1 操作 1 行。outcome は後段の状態コードから決める（拒否・不在・失敗も残す）。
// ■ 🔴 detail は対象 ID と状態コードだけ。**名前・条件などの利用者の入力は載せない**（NFR-28）。
public static class AdminWriteAudit
{
    /// <summary>
    /// 1 行残す。<paramref name="target"/> が無い（作成で応答から ID を読めない）ときは <c>-</c> と残す。
    /// </summary>
    public static void Record(IAuditLogger audit, ClaimsPrincipal user, string action, string? target, int status)
        => audit.Record(action, SubjectOf(user), OutcomeOf(status),
            $"target={(string.IsNullOrWhiteSpace(target) ? "-" : target)} status={status}");

    /// <summary>作成の応答本文（JSON）から <c>id</c> を読む。2xx でない・JSON でない・<c>id</c> が無いときは <c>null</c>。</summary>
    public static string? IdFromCreated(int status, string? body)
    {
        if (status is < 200 or >= 300 || string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && Guid.TryParse(id.GetString(), out var guid)
                    ? guid.ToString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // 利用者名（`AddPlatformAuth` の NameClaimType = preferred_username）。無ければ unknown（記録を落とさない。SC-22 と同じ）。
    public static string SubjectOf(ClaimsPrincipal user) => user.Identity?.Name ?? "unknown";

    public static string OutcomeOf(int status) => status switch
    {
        >= 200 and < 300 => "granted",
        400 or 409 => "denied",
        404 => "not-found",
        _ => "failed",
    };
}
