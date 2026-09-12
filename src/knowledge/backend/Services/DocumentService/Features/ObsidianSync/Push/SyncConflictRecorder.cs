using DocumentService.Domain;
using DocumentService.Features.SyncConflicts;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Audit;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace DocumentService.Features.ObsidianSync.Push;

// FR-20, SC-20 主要素 5, ADR-0037 決定 7・9, [[IADR-0352]], #1442:
// push の版不一致（409）を**利用者が後から解決できる形**で記録する。
//
// 🔴 **プロトコルの応答は変えない。** 409 の状態・本文はそのままであり、プラグインは従来どおり
// 自分で解決できる（`ObsidianSyncProtocolTests` が固定している）。ここが足すのは
// 「SC-20 の競合一覧に出す行」と「差分に見せるローカル本文」だけである。
//
// **同じ資料・同じ端末の未解決競合は上書きする**（行を増やさない）。端末がオフラインのまま
// 何度も push を試すたびに行が増えると、一覧が同じ競合で埋まって解決すべきものが見えなくなる。
internal static class SyncConflictRecorder
{
    // 409 を返す**直前**に呼ぶ。要求の最終本文をオブジェクトストレージへ保存し、行を起こす。
    internal static async Task RecordAsync(DocumentDbContext db, IObjectStorageClient storage,
        IAuditLogger audit, string owner, Guid deviceId, Document doc, int localBaseVersion,
        string localContent, DateTimeOffset now, CancellationToken ct)
    {
        var conflict = await db.SyncConflicts.FirstOrDefaultAsync(
            c => c.DocumentId == doc.Id && c.DeviceId == deviceId && c.ResolvedAt == null, ct);

        if (conflict is null)
        {
            conflict = SyncConflict.Detect(doc.Id, owner, deviceId, localBaseVersion, doc.Version,
                now);
            db.SyncConflicts.Add(conflict);
        }
        else
        {
            conflict.Redetect(localBaseVersion, doc.Version, now);
        }

        // 鍵は競合 ID で固定なので、上書きのときは**本文だけ**が最新へ置き換わる。
        conflict.RecordLocalContent(await storage.PutTextAsync(SyncConflict.StorageKey(conflict.Id),
            localContent, DocumentBodyIntake.ContentType, ct));
        await db.SaveChangesAsync(ct);

        // ADR-0037 決定 9: 監査は「誰が・いつ・何件」。**タイトル・本文・Vault パスは書かない。**
        audit.Record("private-note.sync.conflict", owner, "recorded",
            $"device={deviceId} count=1");
    }

    // 正しい `baseVersion` で push が通ったとき（＝プラグイン側で解決済み）に呼ぶ。
    // **利用者が選んだわけではない**ので、記録用の値 `client` で閉じる（3 択のどれでもない）。
    internal static async Task CloseAsync(DocumentDbContext db, IObjectStorageClient storage,
        string owner, Guid documentId, DateTimeOffset now, CancellationToken ct)
    {
        // 述語は一覧・`syncState` の導出と同じ 1 本である（`SyncConflictEndpoints.Unresolved`）。
        var open = await SyncConflictEndpoints.Unresolved(db, owner)
            .Where(c => c.DocumentId == documentId).ToListAsync(ct);
        if (open.Count == 0) return;

        foreach (var conflict in open)
        {
            // 解決した競合のローカル本文は残さない（解決の 3 択と同じ扱い）。
            if (!string.IsNullOrEmpty(conflict.LocalContentUri))
                await storage.DeleteAsync(conflict.LocalContentUri, ct);
            conflict.Resolve(SyncConflictResolutions.Client, now);
        }
        await db.SaveChangesAsync(ct);
    }
}
