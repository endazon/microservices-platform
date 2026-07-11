using DataSourceService.Api.Foundation.Domain;
using DataSourceService.Api.Foundation.Ports;
using Knowledge.Contracts.Events;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Ports.Storage;
using MassTransit;

namespace DataSourceService.Api.Foundation.Services;

// FR-01, UC-04, IADR-0051: データソース同期のオーケストレーション。
// コネクタ（Discover/Fetch）→オブジェクトストレージ格納→RawDocumentFetched 発行（Map）を束ね、
// 連続失敗の追跡と継続失敗アラート（UC-04 例外フロー）を行う。手動同期（/sync）と定期同期（HostedService）が共用する。
public sealed class DataSourceSyncService(
    ConnectorRegistry registry,
    IObjectStorageClient storage,
    IPublishEndpoint bus,
    SyncFailureTracker failures,
    ILogger<DataSourceSyncService> logger)
{
    // UC-04 例外フロー: 連続失敗がこの回数以上でアラート（継続失敗の警告）。
    public const int AlertThreshold = 3;

    public async Task<SyncResult> SyncAsync(DataSource source, CancellationToken ct = default)
    {
        var result = await RunAsync(source, ct);
        // UC-04 例外フロー: 増分 watermark（LastSyncedAt）は**完全成功時のみ**前進させる（手動/定期で共通）。
        // discover 失敗・一部 fetch 失敗時は進めない。進めてしまうと、失敗/未取得ファイル（更新日時 <= 失敗時刻）が
        // 次回同期の増分から漏れて二度と再取得されず恒久欠落する（＝再試行の担保）。成功済みファイルの再発行は
        // 決定的 DocumentId により下流が冪等 upsert するため安全。
        if (result.ShouldAdvanceWatermark)
            source.RecordSync();
        return result;
    }

    private async Task<SyncResult> RunAsync(DataSource source, CancellationToken ct)
    {
        var connector = registry.Resolve(source.SourceType);
        if (connector is null)
        {
            // 未対応 SourceType（wiki/saas/db 等）は 5xx にせず縮退。子 issue で順次コネクタを追加する。
            logger.LogInformation(
                "SourceType '{Type}' のコネクタは未実装のため同期をスキップします（source {Id}）",
                source.SourceType, source.Id);
            return new SyncResult(0, 0, ConnectorAvailable: false, DiscoverSucceeded: false,
                Message: $"connector for '{source.SourceType}' not implemented");
        }

        IReadOnlyList<SourceItem> items;
        try
        {
            // 増分: 前回同期時刻を watermark に差分のみ列挙（初回は null＝フルスキャン）。
            items = await connector.DiscoverAsync(source, source.LastSyncedAt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AlertOnFailure(source, "discover", ex);
            // discover 失敗は「成功して 0 件」と区別する（DiscoverSucceeded=false）→ watermark を進めない。
            return new SyncResult(0, 0, ConnectorAvailable: true, DiscoverSucceeded: false,
                Message: "discover failed: " + ex.Message);
        }

        // Map: データソースの既定 ABAC 属性（機密区分フェイルセーフ含む・IADR-0019）を原本へ付与する。
        var attributes = source.GetEffectiveAttributes();
        var fetched = 0;
        var failed = 0;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var raw = await connector.FetchAsync(source, item, ct);
                var fetchId = Guid.NewGuid();
                // 原本をオブジェクトストレージへ格納（未構成時は NullObjectStorageClient が決定的 URI を返し縮退）。
                var key = $"{source.Id}/{fetchId}/raw{Path.GetExtension(item.Path)}";
                var storageUri = await storage.PutBytesAsync(key, raw.Bytes, raw.ContentType, ct);

                await bus.Publish(new RawDocumentFetched(
                    fetchId, source.Id, source.SourceType,
                    item.Path, storageUri, raw.ContentType,
                    new Dictionary<string, string>(attributes), BuildTags(item),
                    DateTimeOffset.UtcNow), ct);
                fetched++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogWarning(ex, "原本の取得/発行に失敗: {Path}（source {Id}）", item.Path, source.Id);
            }
        }

        if (failed == 0)
            failures.Reset(source.Id);
        else
            AlertOnFailure(source, $"{failed}/{items.Count} 件の取得に失敗", null);

        logger.LogInformation(
            "同期完了 source {Id}（{Type}）: fetched={Fetched} failed={Failed}",
            source.Id, source.SourceType, fetched, failed);
        return new SyncResult(fetched, failed, ConnectorAvailable: true, DiscoverSucceeded: true, Message: null);
    }

    // Map: フォルダ名をタグへ（ソースメタの粗マッピング）。機密区分等の ABAC 属性は Attributes 側で運搬する。
    private static List<string> BuildTags(SourceItem item)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(item.Path));
        return string.IsNullOrWhiteSpace(folder) ? [] : [folder];
    }

    // UC-04 例外フロー: 連続失敗を記録し、閾値超過で継続失敗アラート（構造化ログ Alert=true）を出す。
    private void AlertOnFailure(DataSource source, string phase, Exception? ex)
    {
        var count = failures.RecordFailure(source.Id);
        if (ex is not null)
            logger.LogWarning(ex, "同期失敗（{Phase}）source {Id} 連続{Count}回", phase, source.Id, count);

        if (count >= AlertThreshold)
            logger.LogError(
                "継続失敗アラート: データソース {Name}（{Id}）が {Count} 回連続で同期に失敗しています（{Phase}） {Alert}",
                source.Name, source.Id, count, phase, true);
    }
}

// 同期結果。ConnectorAvailable=false は未対応 SourceType（縮退）。DiscoverSucceeded=false は
// discover が失敗した（＝「成功して 0 件」と区別する）ことを表す。
public sealed record SyncResult(
    int Fetched, int Failed, bool ConnectorAvailable, bool DiscoverSucceeded, string? Message)
{
    // 増分 watermark（LastSyncedAt）を進めてよいのは、コネクタがあり discover が成功し、
    // 全アイテムの取得に成功したとき。失敗があれば進めず、次回同期で再試行できるようにする（UC-04 再試行）。
    public bool ShouldAdvanceWatermark => ConnectorAvailable && DiscoverSucceeded && Failed == 0;
}
