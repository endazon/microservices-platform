using DataSourceService.Api.Foundation.Domain;
using DataSourceService.Api.Foundation.Persistence;
using DataSourceService.Api.Foundation.Services;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Extensions;
using Microsoft.EntityFrameworkCore;

namespace DataSourceService.Api.Foundation.Endpoints;

// FR-01, UC-04: データソース管理エンドポイント
public static class DataSourceEndpoints
{
    public static IEndpointRouteBuilder MapDataSourceEndpoints(this IEndpointRouteBuilder app)
    {
        // FR-09, IADR-0044: 多層防御。データソースは運用資産で、閲覧・操作は管理者・運用者に限定する
        // （[[IADR-0039]] の BFF ゲートと同一要件）。BFF 迂回の直接呼び出しでも認可を実効化する
        // （サービスが最終防衛線）。利用者トークンは BFF が後段へ伝播する。
        var g = app.MapGroup("/datasources").WithTags("DataSources")
            .RequireAuthorization(p => p.RequireRole(
                KnowledgePlatformAuthPolicies.AdminRole,
                KnowledgePlatformAuthPolicies.OperatorRole));

        g.MapGet("/", async (DataSourceDbContext db) =>
            Results.Ok(await db.DataSources.ToListAsync()));

        g.MapGet("/{id:guid}", async (Guid id, DataSourceDbContext db) =>
        {
            var ds = await db.DataSources.FindAsync(id);
            return ds is null ? Results.NotFound() : Results.Ok(ds);
        });

        g.MapPost("/", async (CreateDataSourceRequest req, DataSourceDbContext db) =>
        {
            // FR-01, FR-05: 既定 ABAC 属性（機密区分）を伴ってデータソースを登録する。
            var ds = DataSource.Create(req.Name, req.SourceType, req.ConnectionUri,
                req.Config, req.DefaultAttributes);
            db.DataSources.Add(ds);
            await db.SaveChangesAsync();
            return Results.Created($"/datasources/{ds.Id}", ds);
        });

        // FR-01, UC-04: 手動同期トリガー（IADR-0051）。実コネクタ経由で原本を取得・格納し
        // RawDocumentFetched を発行する。既定 ABAC 属性（機密区分・IADR-0019）は同期サービスが Map で付与する。
        // 未対応 SourceType（wiki/saas/db）はコネクタ未実装のため縮退（連携件数 0・5xx にしない）。
        g.MapPost("/{id:guid}/sync", async (Guid id, DataSourceDbContext db,
            DataSourceSyncService sync) =>
        {
            var ds = await db.DataSources.FindAsync(id);
            if (ds is null) return Results.NotFound();

            var result = await sync.SyncAsync(ds);
            // 増分同期の watermark を進める（次回はこの時刻以降の変更のみ取得）。
            ds.RecordSync();
            await db.SaveChangesAsync();

            return Results.Accepted($"/datasources/{id}/sync", new
            {
                fetched = result.Fetched,
                failed = result.Failed,
                connectorAvailable = result.ConnectorAvailable,
                message = result.Message,
            });
        });

        g.MapDelete("/{id:guid}", async (Guid id, DataSourceDbContext db) =>
        {
            var ds = await db.DataSources.FindAsync(id);
            if (ds is null) return Results.NotFound();
            ds.Disable();
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return app;
    }
}

public record CreateDataSourceRequest(
    string Name,
    string SourceType,
    string ConnectionUri,
    Dictionary<string, string>? Config,
    // FR-05: 原本へ付与する既定 ABAC 文書属性（confidentiality 等）。未指定時は internal を補完。
    Dictionary<string, string>? DefaultAttributes = null);
