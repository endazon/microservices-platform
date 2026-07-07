using KnowledgePlatform.Shared.Contracts.Dtos;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Audit;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Extensions;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Introspection;

namespace KnowledgePlatform.Bff.Foundation.Endpoints;

// FR-15, ADR-0018: 構成情報 API（読み取り専用）。実効構成とドリフトを管理者・運用者へ返す。
// 独立サービス化せず BFF 配下の管理 API として同居させる（過剰分割回避。IADR に記録）。
// 閲覧は管理者・運用者ロールに限定し、非権限は 404 で応答自体を秘匿する（存在秘匿の方針と整合）。
// 取得操作は許可・拒否ともに監査ログへ記録する。
public static class ConfigBffEndpoints
{
    public static IEndpointRouteBuilder MapConfigBffEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/bff/admin/config").WithTags("Config BFF");

        // FR-15: 現在有効な実効構成（段・接続・ポート選択・コネクタ・構成バージョン）を返す。
        g.MapGet("", async (
            HttpContext http,
            IConfigInspectionService inspection,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (Deny(http, audit, "config.read", out var denied))
                return denied;

            var config = await inspection.GetEffectiveConfigAsync(ct);
            return Results.Ok(config);
        }).WithName("BffConfigEffective")
          .RequireAuthorization()
          .Produces<EffectiveConfigDto>();

        // FR-15: 宣言（Git）と実効構成の不一致（ドリフト）を返す。
        g.MapGet("/drift", async (
            HttpContext http,
            IConfigInspectionService inspection,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (Deny(http, audit, "config.drift.read", out var denied))
                return denied;

            var drift = await inspection.GetDriftAsync(ct);
            return Results.Ok(drift);
        }).WithName("BffConfigDrift")
          .RequireAuthorization()
          .Produces<DriftReportDto>();

        return app;
    }

    // 閲覧権限（管理者・運用者）を検査する。非権限は監査へ「denied」を記録し 404 で存在を秘匿する。
    // 権限ありは「granted」を記録して続行する。
    private static bool Deny(HttpContext http, IAuditLogger audit, string action, out IResult result)
    {
        var subject = http.User.Identity?.Name ?? "unknown";
        var authorized = http.User.IsInRole(KnowledgePlatformAuthPolicies.AdminRole)
            || http.User.IsInRole(KnowledgePlatformAuthPolicies.OperatorRole);

        if (!authorized)
        {
            audit.Record(action, subject, "denied");
            result = Results.NotFound();
            return true;
        }

        audit.Record(action, subject, "granted");
        result = Results.Empty;
        return false;
    }
}
