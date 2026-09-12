using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace DocumentService.Features.ObsidianSync;

// FR-20, UC-11, SC-20 主要素 6, ADR-0037 決定 9, ADR-0099, #1446（planning#618 の裁定）:
// 同期の実行記録を**同じ内容で 2 つの出口へ**出す（貯蔵を持つ監査ログ）。
//   ① `SyncAuditEntry`（表）… SC-20 の同期履歴が本人スコープで読む（ADR-0099 決定 1・2）
//   ② `audit.Record("private-note.sync.<op>", ...)`（構造化ログ → OTel）… 従前の監査ログ（決定 9）
// **②を別々に書かない。** 記録の出口を 1 つの関数に閉じることで「表には在るがログに無い」
// （またはその逆）を構造的に起こさなくする —— 従前は各経路が②だけを直に呼んでいた。
//
// 🔴 **detail に題名・Vault パスを書かない**（ADR-0037 決定 9・決定 3 / ADR-0099 決定 5）。
// 足すのは端末・方向・内訳・理由だけである。既存の `count=` / `versions=` は `extra` で
// 呼び出し元が引き継ぐ（既存の試験と運用のクエリが読んでいる値を消さない）。
//
// 🔴 **401（端末が解決できない）では呼ばない。** 所有者が決まらないので、行に書く主体が無い。
// Manifest（一覧取得）も従前どおり記録しない —— 同期ではない。
internal static class SyncAuditRecorder
{
    // ADR-0099 決定 5: 方向は操作から決まる（端末 → サーバが `push`、サーバ → 端末が `pull`）。
    // **呼び出し元に方向と操作の 2 つを渡させない** —— 2 つ渡すと組み合わせを間違えられる。
    internal static string DirectionOf(string op)
        => op == SyncOps.Pull ? SyncDirections.Pull : SyncDirections.Push;

    // 成功の記録。**行を Add するだけで SaveChanges しない** —— 呼び出し元が資料の変更と
    // 同じ `SaveChangesAsync` で確定させる（**同じトランザクション**。同期が成功したのに
    // 履歴が無い／履歴が在るのに資料が変わっていない、のどちらも起こさない）。
    internal static void Success(DocumentDbContext db, IAuditLogger audit, string owner,
        SyncDevice device, string op, int added, int updated, int deleted,
        DateTimeOffset now, string? extra = null)
    {
        var direction = DirectionOf(op);
        db.SyncAuditEntries.Add(SyncAuditEntry.Success(owner, device.Id, device.DeviceName,
            direction, added, updated, deleted, now));

        audit.Record(ActionOf(op), owner, "granted",
            Detail(device.Id, direction, added, updated, deleted, conflicted: 0, reason: null,
                extra: extra));
    }

    // 失敗の記録。**Add ＋ SaveChanges する** —— 失敗経路には他に保存するものが無い。
    //
    // 🔴 **不変条件: 呼ぶのは「まだ資料へ変更を加えていない」早期 return の直前だけである。**
    // 変更を追跡した状態（版の適用・台帳の更新の後）で呼ぶと、この `SaveChangesAsync` が
    // **拒否したはずの変更ごと確定させてしまう**（409 を返しながらサーバ本文が書き換わる）。
    // 現在の呼び出し元はすべて検証・照会だけを済ませた地点であり、その形を崩してはならない。
    // push の版不一致だけは直前に `SyncConflictRecorder.RecordAsync` が走るが、あれが確定させるのは
    // **競合の行だけ**であり（資料の変更ではない）、この不変条件を破らない。
    internal static async Task FailureAsync(DocumentDbContext db, IAuditLogger audit, string owner,
        SyncDevice device, string op, string reason, int conflicted, DateTimeOffset now,
        CancellationToken ct)
    {
        var direction = DirectionOf(op);
        db.SyncAuditEntries.Add(SyncAuditEntry.Failure(owner, device.Id, device.DeviceName,
            direction, reason, conflicted, now));
        await db.SaveChangesAsync(ct);

        audit.Record(ActionOf(op), owner, "denied",
            Detail(device.Id, direction, added: 0, updated: 0, deleted: 0, conflicted: conflicted,
                reason: reason, extra: null));
    }

    // 監査ログの action。**経路ごとの既存の名前をそのまま保つ**（運用のクエリと既存の試験が読む）。
    private static string ActionOf(string op) => $"private-note.sync.{op}";

    // 🔴 **題名・パスは載せない。** 載せるのは端末・方向・内訳・理由だけである。
    private static string Detail(Guid deviceId, string direction, int added, int updated,
        int deleted, int conflicted, string? reason, string? extra)
        => $"device={deviceId} direction={direction} added={added} updated={updated}"
         + $" deleted={deleted} conflicted={conflicted}"
         + (reason is null ? string.Empty : $" reason={reason}")
         + (string.IsNullOrEmpty(extra) ? string.Empty : $" {extra}");
}

// FR-20, ADR-0099: 同期の操作種別（監査ログの action の末尾。`SyncDirections` とは別物である
// —— delete と move はどちらも方向 `push` だが、監査上は別の操作として残す）。
internal static class SyncOps
{
    internal const string Push = "push";
    internal const string Pull = "pull";
    internal const string Delete = "delete";
    internal const string Move = "move";
}
