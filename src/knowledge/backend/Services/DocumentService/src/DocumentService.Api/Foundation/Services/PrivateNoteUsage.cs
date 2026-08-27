using System.Security.Cryptography;
using System.Text;
using DocumentService.Api.Foundation.Domain;
using DocumentService.Api.Foundation.Persistence;
using DocumentService.Api.Foundation.Ports;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Api.Foundation.Services;

// FR-20, ADR-0037 決定 11〜13・15, [[IADR-0270]] 決定 3: 同期トークンの生成とハッシュ化。
// 平文は発行応答で 1 回だけ返し、保存は SHA-256（hex）のみ。
public static class SyncTokens
{
    // 256bit の乱数を URL 安全な hex で払い出す。プラグイン設定への貼り付けを想定する。
    public static (string Token, string Hash) Generate()
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        return (token, HashOf(token));
    }

    public static string HashOf(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

// FR-19, NFR-27, ADR-0037 決定 16・17・19, [[IADR-0270]] 決定 4: 保存容量の算出と警告の発火。
public static class PrivateNoteUsage
{
    // 使用量 = 全台帳行（論理削除済みを含む）の最新版バイト数の合計。
    // 版履歴は台帳がバイト数を持たないため構造的に算入されない（決定 16）。
    // purge 済みは行ごと消えているため算入されない（決定 19 の対）。
    public static async Task<long> UsedBytesAsync(DocumentDbContext db, string ownerId,
        CancellationToken ct = default)
        => await db.PrivateNotes.Where(n => n.OwnerId == ownerId)
            .SumAsync(n => (long?)n.LatestBytes, ct) ?? 0L;

    public static async Task<PrivateNoteQuota> GetOrCreateQuotaAsync(DocumentDbContext db,
        string ownerId, DateTimeOffset now, CancellationToken ct = default)
    {
        var quota = await db.PrivateNoteQuotas.FindAsync([ownerId], ct);
        if (quota is null)
        {
            quota = PrivateNoteQuota.Create(ownerId, now);
            db.PrivateNoteQuotas.Add(quota);
        }
        return quota;
    }

    // FR-22 ②: 使用量を再計算し、80% / 95% の跨ぎがあれば各 1 回通知を発火する。
    //
    // ★［2026-08-28 変更 / #600］**発火記録（Warned80 / Warned95）を送出より先に確定させる**
    // （IADR-0215 決定 5-a）。従前は呼び出し側の SaveChanges に委ねていたため、
    // **送出後・保存前にプロセスが落ちると次の再計算で同じ警告をもう一度送った**。
    // 受け口の重複抑止はペイロード 6 項目の完全一致でしか畳まず、`occurredAt` が変わる再検知は
    // 畳まれないため、**重複を止められるのは発火側だけ**である。
    // 受容するトレードオフ: **記録が残ったのに送出に失敗した警告は再送されない**
    // （閾値を下回れば再武装されるので、②は自然に回復する）。
    //
    // 🔴 **本メソッドは自ら SaveChanges する。** 3 つの呼び出し点（同期 push の新規／更新・
    // 完全削除の端点・自動 purge）はいずれも本体の業務処理を先に確定させてから呼ぶため、
    // ここでの確定が業務処理のトランザクションを分断することは無い。
    public static async Task RecordUsageAndWarnAsync(DocumentDbContext db,
        IPrivateNoteNotifier notifier, string ownerId, DateTimeOffset now,
        CancellationToken ct = default)
    {
        var used = await UsedBytesAsync(db, ownerId, ct);
        var quota = await GetOrCreateQuotaAsync(db, ownerId, now, ct);
        var crossed = quota.RecordUsage(used, now);
        await db.SaveChangesAsync(ct);

        foreach (var threshold in crossed)
        {
            await notifier.NotifyAsync(ownerId, PrivateNoteNotificationKinds.StorageQuotaWarning,
                now, thresholdPercent: threshold, ct: ct);
        }
    }
}
