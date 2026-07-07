using KnowledgePlatform.Shared.Contracts.Dtos;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Audit;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Extensions;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Introspection;
using Microsoft.AspNetCore.Authorization;

namespace KnowledgePlatform.Bff.Foundation.Endpoints;

// FR-15, ADR-0018: 構成情報 API（読み取り専用）。実効構成とドリフトを管理者・運用者へ返す。
// 独立サービス化せず BFF 配下の管理 API として同居させる（過剰分割回避。IADR に記録）。
// 閲覧は ConfigViewer ポリシー（管理者・運用者）に限定し、非権限（無認証を含む一般利用者）は
// 404 で応答自体を秘匿する（IADR-0009 の存在秘匿と整合）。RequireAuthorization を付けると
// 無認証が 404 到達前に 401 で短絡し存在が漏れるため、認可はハンドラ内で判定する。
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
            IAuthorizationService authz,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            var denied = await DenyAsync(http, authz, audit, "config.read");
            if (denied is not null)
                return denied;

            var config = await inspection.GetEffectiveConfigAsync(ct);
            return Results.Ok(config);
        }).WithName("BffConfigEffective")
          .Produces<EffectiveConfigDto>();

        // FR-15: 宣言（Git）と実効構成の不一致（ドリフト）を返す。
        g.MapGet("/drift", async (
            HttpContext http,
            IConfigInspectionService inspection,
            IAuthorizationService authz,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            var denied = await DenyAsync(http, authz, audit, "config.drift.read");
            if (denied is not null)
                return denied;

            var drift = await inspection.GetDriftAsync(ct);
            return Results.Ok(drift);
        }).WithName("BffConfigDrift")
          .Produces<DriftReportDto>();

        return app;
    }

    // 閲覧権限を ConfigViewer ポリシー（管理者・運用者）で検査する。無認証を含む非権限は監査へ
    // 「denied」を記録し 404 で存在を秘匿する（→ 拒否時のみ IResult を返す）。権限ありは「granted」を
    // 記録し null を返して続行する。RequireAuthorization を使わず、無認証でも 401 で短絡させない。
    private static async Task<IResult?> DenyAsync(
        HttpContext http, IAuthorizationService authz, IAuditLogger audit, string action)
    {
        var subject = http.User.Identity?.Name ?? "unknown";
        var authorized =
            (await authz.AuthorizeAsync(http.User, KnowledgePlatformAuthPolicies.ConfigViewer)).Succeeded;

        if (!authorized)
        {
            audit.Record(action, subject, "denied");
            return Results.NotFound();
        }

        audit.Record(action, subject, "granted");
        return null;
    }
}
