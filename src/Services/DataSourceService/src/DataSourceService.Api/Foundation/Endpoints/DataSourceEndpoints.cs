using DataSourceService.Api.Foundation.Domain;
using DataSourceService.Api.Foundation.Persistence;
using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace DataSourceService.Api.Foundation.Endpoints;

// FR-01, UC-04: データソース管理エンドポイント
public static class DataSourceEndpoints
{
    public static IEndpointRouteBuilder MapDataSourceEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/datasources").WithTags("DataSources");

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

        // FR-01, UC-04: 手動同期トリガー（実際はポーリングジョブが行う）
        g.MapPost("/{id:guid}/sync", async (Guid id, DataSourceDbContext db,
            IPublishEndpoint bus) =>
        {
            var ds = await db.DataSources.FindAsync(id);
            if (ds is null) return Results.NotFound();

            // シミュレート: 原本取得イベント発行（実装では実際にファイルを取得する）
            var fetchId = Guid.NewGuid();
            // FR-01, FR-05: データソースの既定 ABAC 属性（機密区分）を原本へ付与して発行する。
            // GetEffectiveAttributes() で機密区分欠落をフェイルセーフ補完する。既存（マイグレーション前）
            // の空属性データソースでも、空のまま流して下流の fail-closed 検索（IADR-0012）で文書が
            // 検索結果から除外されるのを防ぐ（IADR-0019）。
            await bus.Publish(new RawDocumentFetched(
                fetchId, ds.Id, ds.SourceType,
                "/sample/path/document.docx",
                $"storage://{ds.Id}/{fetchId}/raw",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ds.GetEffectiveAttributes(), [], DateTimeOffset.UtcNow));

            ds.RecordSync();
            await db.SaveChangesAsync();
            return Results.Accepted($"/datasources/{id}/sync/{fetchId}", new { fetchId, status = "queued" });
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
