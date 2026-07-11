using DataSourceService.Api.Foundation.Domain;
using DataSourceService.Api.Foundation.Persistence;
using DataSourceService.Api.Foundation.Services;
using Platform.Shared.Infrastructure.Foundation.Extensions;
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
                PlatformAuthPolicies.AdminRole,
                PlatformAuthPolicies.OperatorRole));

        // IADR-0053, claude-review #222: 応答では Config 内の秘密（apiToken 等）をマスクする。
        // Vault 移行までの暫定措置。admin/operator であっても API 応答で平文の資格情報を露出させない。
        g.MapGet("/", async (DataSourceDbContext db) =>
            Results.Ok((await db.DataSources.ToListAsync()).Select(ToResponse)));

        g.MapGet("/{id:guid}", async (Guid id, DataSourceDbContext db) =>
        {
            var ds = await db.DataSources.FindAsync(id);
            return ds is null ? Results.NotFound() : Results.Ok(ToResponse(ds));
        });

        g.MapPost("/", async (CreateDataSourceRequest req, DataSourceDbContext db) =>
        {
            // FR-01, FR-05: 既定 ABAC 属性（機密区分）を伴ってデータソースを登録する。
            var ds = DataSource.Create(req.Name, req.SourceType, req.ConnectionUri,
                req.Config, req.DefaultAttributes);
            db.DataSources.Add(ds);
            await db.SaveChangesAsync();
            return Results.Created($"/datasources/{ds.Id}", ToResponse(ds));
        });

        // FR-01, UC-04: 手動同期トリガー（IADR-0051）。実コネクタ経由で原本を取得・格納し
        // RawDocumentFetched を発行する。既定 ABAC 属性（機密区分・IADR-0019）は同期サービスが Map で付与する。
        // 未対応 SourceType（wiki/saas/db）はコネクタ未実装のため縮退（連携件数 0・5xx にしない）。
        g.MapPost("/{id:guid}/sync", async (Guid id, DataSourceDbContext db,
            DataSourceSyncService sync) =>
        {
            var ds = await db.DataSources.FindAsync(id);
            if (ds is null) return Results.NotFound();

            // 増分 watermark（LastSyncedAt）の前進は SyncAsync が完全成功時のみ実施する
            // （失敗時は進めず次回再試行。UC-04 例外フロー）。ここでは永続化のみ行う。
            var result = await sync.SyncAsync(ds);
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

    // IADR-0053, claude-review #222: API 応答用の投影。エンティティをそのまま返すと Config 内の
    // 秘密（apiToken 等）が平文露出するため、秘密キーの値をマスクして返す（Vault 移行までの暫定）。
    private static object ToResponse(DataSource ds) => new
    {
        ds.Id,
        ds.Name,
        ds.SourceType,
        ds.ConnectionUri,
        ds.Status,
        ds.LastSyncedAt,
        Config = RedactSecrets(ds.Config),
        ds.DefaultAttributes,
        ds.CreatedAt,
    };

    // 秘密とみなすキー名の部分一致マーカー（大文字小文字無視）。spaceKey/listPath/rootPath 等は誤マスクしない。
    private static readonly string[] SecretKeyMarkers = ["token", "password", "secret", "credential"];

    private static Dictionary<string, string> RedactSecrets(IReadOnlyDictionary<string, string> config)
        => config.ToDictionary(
            kv => kv.Key,
            kv => SecretKeyMarkers.Any(m => kv.Key.Contains(m, StringComparison.OrdinalIgnoreCase))
                ? "***"
                : kv.Value);
}

public record CreateDataSourceRequest(
    string Name,
    string SourceType,
    string ConnectionUri,
    Dictionary<string, string>? Config,
    // FR-05: 原本へ付与する既定 ABAC 文書属性（confidentiality 等）。未指定時は internal を補完。
    Dictionary<string, string>? DefaultAttributes = null);
