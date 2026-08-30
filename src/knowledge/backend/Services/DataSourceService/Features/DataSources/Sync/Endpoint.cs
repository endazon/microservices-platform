using DataSourceService.Infrastructure.Persistence;

namespace DataSourceService.Features.DataSources.Sync;

// FR-01, UC-04: 手動同期トリガー（IADR-0051）。実コネクタ経由で原本を取得・格納し
// RawDocumentFetched を発行する。既定 ABAC 属性（機密区分・IADR-0019）は同期サービスが Map で付与する。
// filesystem/wiki/saas/db コネクタは実装・DI 登録済み（#195/#217/#218/#219）。未登録の SourceType は
// 縮退（連携件数 0・5xx にしない）。新規コネクタはプラグイン（DI 登録）追加のみで対応する（IADR-0051）。
//
// **認可はグループ既定（admin ＋ operator）のままである**（#628 / planning#299 で裁定・2026-08-09）。
// 計画は手動同期を**破壊的操作に含めない** —— 外部システムへ接続して取り込みを走らせるが、
// 増分同期と変換の冪等性により既存データを壊さないためであり、**運用者が SC-10 で異常に気づいた
// その場で再同期して一次対応できること**を優先する。**登録・無効化と同じに扱わないこと。**
// ［範囲］人手補正（Phase 2）の導入時に本分類を再確認する（UC-06 の「再変換は補正を破棄する前に警告」が
// 手動同期由来の再変換にも及ぶかは未確定。補正投稿 API が未実装なので現時点では実害が無い）。
//
// ADR-0068 決定 2: 手動起動（本端点）と定期起動（`DataSourceSyncHostedService`）は**同じ
// 原本同期（UC-04）の処理**であり、`DataSourceSyncService` を含めて 1 つの操作フォルダに揃う。
internal static class SyncDataSourceEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
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
    }
}
