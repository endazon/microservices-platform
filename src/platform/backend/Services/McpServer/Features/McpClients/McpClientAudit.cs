using System.Security.Claims;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace McpServer.Features.McpClients;

// FR-16, UC-09, SC-12, 計画 ADR-0134 決定 2 の 2・3・フォローアップ 5, ADR-0004, [[IADR-0516]]（2026-10-09 追記 / #1845）:
// SC-12 の管理操作の監査記録。**器は既存の `IAuditLogger`**（`Audit=true` の構造化ログ → OTel → ログ基盤。BFF の SC-22 と同じ）であり、
// 新しい記録先は作らない。記録は**この 1 か所**から出す（操作ごとに書き方が揃わないと、ログ基盤での引き方が割れる）。
//
// ■ 何を残すか: 誰が（利用者名＝`preferred_username`）・いつ（ログの時刻）・どの操作（action）・どのクライアント（detail の `client=`）・
//   結果（outcome。拒否・不在・未構成・失敗も残す）。
// ■ 🔴 **secret の値は残さない**（決定 2 の 2・3）。detail に渡すのは clientId・種別・状態コード・割り当てた ABAC 属性だけで、
//   secret を受け取る引数を持たない。値が監査にもアプリケーションのログにも出ないことは `McpClientSecretLeakTests` が固定する。
// ■ 🔴 **削除は記録しない** —— SC-12 に削除の操作が無い（登録簿の削除の API は無い）。足すときはここに action を足して同じ器で残す。
// ■ 認可で弾かれた要求（管理者でない 403）は端点に届かないので、ここでは記録しない。
internal static class McpClientAudit
{
    public const string RegisterAction = "mcp-client.register";
    public const string ReplaceAttributesAction = "mcp-client.replace-attributes";
    public const string DisableAction = "mcp-client.disable";
    public const string EnableAction = "mcp-client.enable";
    public const string SecretIssueAction = "mcp-client.secret.issue";
    public const string SecretReissueAction = "mcp-client.secret.reissue";

    /// <summary>
    /// <paramref name="run"/> を実行し、結果の状態コードから outcome を決めて 1 行残す。例外は <c>failed</c> を残してから投げ直す
    /// （登録簿への書き込みの例外は `IdpFirstWrite` が補償してから投げる。ホストの既定で 500 になる）。
    /// </summary>
    public static async Task<IResult> RecordAsync(
        IAuditLogger audit, ClaimsPrincipal user, string action, string detail, Func<Task<IResult>> run)
    {
        IResult result;
        try
        {
            result = await run();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            audit.Record(action, SubjectOf(user), "failed", $"{detail} status=500");
            throw;
        }

        var status = StatusOf(result);
        audit.Record(action, SubjectOf(user), OutcomeOf(status), $"{detail} status={status}");
        return result;
    }

    /// <summary>無人の登録が成功したとき、登録の行とは別に「secret を発行した」を残す（値は残さない）。</summary>
    public static void RecordIssued(IAuditLogger audit, ClaimsPrincipal user, string action, string clientId)
        => audit.Record(action, SubjectOf(user), "granted", $"client={clientId}");

    public static string Detail(string clientId, string? kind = null, IReadOnlyDictionary<string, string>? attributes = null)
    {
        var detail = $"client={clientId}";
        if (kind is not null) detail += $" kind={kind}";
        if (attributes is not null)
            detail += " attributes=" + string.Join(';', attributes.OrderBy(a => a.Key, StringComparer.Ordinal)
                .Select(a => $"{a.Key}={a.Value}"));
        return detail;
    }

    // 利用者名（`AddPlatformAuth` の NameClaimType = preferred_username）。無ければ「不明」と残す（記録を落とさない）。
    internal static string SubjectOf(ClaimsPrincipal user)
        => string.IsNullOrWhiteSpace(user.Identity?.Name) ? "(不明)" : user.Identity!.Name!;

    internal static int StatusOf(IResult result)
        => result is IStatusCodeHttpResult { StatusCode: { } code } ? code : StatusCodes.Status200OK;

    internal static string OutcomeOf(int status) => status switch
    {
        >= 200 and < 300 => "granted",
        StatusCodes.Status400BadRequest => "denied",
        StatusCodes.Status404NotFound => "not-found",
        StatusCodes.Status503ServiceUnavailable => "unavailable",
        _ => "failed",
    };
}
