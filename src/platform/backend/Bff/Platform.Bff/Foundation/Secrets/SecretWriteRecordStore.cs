using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace Platform.Bff.Foundation.Secrets;

// SC-22 主要素 1「最終更新者」, IADR-0453 決定 3 (#1411):
// **BFF が自分で書いた版**の記録。Vault KV v2 の metadata は更新者を持たず、
// `custom_metadata` へ書くには metadata の書き込み権限が要る（IADR-0433 決定 1 を広げる）ため、
// Vault の権限は変えずに BFF 側へ置く。
//
// 🔴 **表示は Vault の現在版と一致するときだけ**（呼び出し側が突き合わせる）。
// コンソール・bootstrap が後から書いた版に、画面の利用者名が付くことは無い。
// IADR-0454 決定 3 (#1467): 突き合わせは**版の番号と作成時刻（`UpdatedAt` ＝ 書いた版の `created_time`）の両方**で行う。
// metadata を作り直すと番号は 1 から振り直されるためである。**TTL は置かない**（正しい名前まで期限で消えるため）。
// 🔴 **正本は監査ログである。** 本記録は 1 列を出すための付随物で、消えたら「記録なし」へ倒れる。
public sealed record SecretWriteRecord(int Version, string UpdatedBy, string Property, DateTimeOffset UpdatedAt);

public interface ISecretWriteRecordStore
{
    Task<SecretWriteRecord?> GetAsync(string item, CancellationToken ct);

    Task SaveAsync(string item, SecretWriteRecord record, CancellationToken ct);
}

// 置き場は BFF セッションと同じ Redis（`IDistributedCache`）。キーの接頭辞でセッションと分ける。
public sealed class DistributedCacheSecretWriteRecordStore(
    IDistributedCache cache,
    ILogger<DistributedCacheSecretWriteRecordStore> logger) : ISecretWriteRecordStore
{
    internal const string KeyPrefix = "bff:sc22:write-record:";

    public async Task<SecretWriteRecord?> GetAsync(string item, CancellationToken ct)
    {
        try
        {
            var bytes = await cache.GetAsync(KeyPrefix + item, ct);
            return bytes is null ? null : JsonSerializer.Deserialize<SecretWriteRecord>(bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // 🔴 握って「記録なし」へ倒す。記録が引けないことを一覧の失敗にしない。
            logger.LogWarning(ex, "SC-22 の書き込み記録を読めない（最終更新者は「記録なし」になる）");
            return null;
        }
    }

    public async Task SaveAsync(string item, SecretWriteRecord record, CancellationToken ct)
    {
        try
        {
            await cache.SetAsync(KeyPrefix + item, JsonSerializer.SerializeToUtf8Bytes(record),
                new DistributedCacheEntryOptions(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // 🔴 握る。書き込みは Vault で既に成立しており、記録が置けないことを 5xx にすると
            // 「書けたのに失敗と表示される」ことになる。
            logger.LogWarning(ex, "SC-22 の書き込み記録を置けない（最終更新者は「記録なし」になる）");
        }
    }
}
