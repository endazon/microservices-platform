using System.Collections.Concurrent;

namespace DataSourceService.Api.Foundation.Services;

// FR-01, UC-04（例外フロー）, IADR-0051: データソース単位の連続同期失敗回数を追跡する。
// 継続失敗アラートの判定に用いる（本 PR ではインメモリ。DB 永続化は follow-up）。singleton で共有する。
public sealed class SyncFailureTracker
{
    private readonly ConcurrentDictionary<Guid, int> _failures = new();

    // 失敗を記録し、更新後の連続失敗回数を返す。
    public int RecordFailure(Guid sourceId)
        => _failures.AddOrUpdate(sourceId, 1, (_, count) => count + 1);

    // 成功時に連続失敗回数をリセットする。
    public void Reset(Guid sourceId) => _failures.TryRemove(sourceId, out _);

    // 現在の連続失敗回数（テスト・可観測性向け）。
    public int Current(Guid sourceId) => _failures.GetValueOrDefault(sourceId);
}
