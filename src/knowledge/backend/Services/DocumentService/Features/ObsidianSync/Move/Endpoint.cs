using DocumentService.Features.PrivateNotes;
using DocumentService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace DocumentService.Features.ObsidianSync.Move;

// FR-20, ADR-0037 決定 2・7・9・14, [[IADR-0353]]: リネーム（Obsidian 側の名前変更の伝播）。
//
// **名前だけを動かす口である**（本文は push が運ぶ）。更新 push へ相乗りさせない理由は
// [[IADR-0353]] 決定 1 —— 現行クライアントは更新 push の `vaultPath` に追跡中のローカルパスを
// 毎回載せており、サーバがそれを採り始めると **サーバ側リネームをまだ pull していない端末の
// push が新しい名前を旧名へ巻き戻す**。名前と中身を別々に拒否できる形にする。
//
// 🔴 **版は進めない**（決定 2）。`VaultPath` は台帳（PrivateNote）の項目であって `Document` の版では
// ない。それでも `version` は必須にし、現在版と違えば 409 —— 古い認識のまま名前を動かさせない
// （決定 7 と同じ向き。サーバは自動解決しない）。
//
// 🔴 **監査に `vaultPath` を書かない**（決定 3）。個人資料の `vaultPath` はファイル名であり
// 実質的に題名である。決定 9 が禁じる「タイトル」の抜け道になる。
internal static class MoveNoteEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/notes/{id:guid}/move", async (Guid id, MoveNoteRequest req, HttpContext http,
            DocumentDbContext db, IAuditLogger audit, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var device = await ObsidianSyncEndpoints.ResolveDeviceAsync(http, db, now, ct);
            if (device is null) return Results.Unauthorized();
            var owner = device.OwnerId;

            if (string.IsNullOrWhiteSpace(req.VaultPath))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["vaultPath"] = ["移動先の vaultPath を指定してください。"]
                });
            if (req.Version is not { } version)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["version"] = ["リネームには version（最後に見た版）が必須です。"]
                });

            // 所有者スコープ外・不在はいずれも 404（存在秘匿。403 を返すと他人の資料 ID の実在が漏れる）。
            var note = await ObsidianSyncEndpoints.FindOwnedAsync(db, owner, id, ct);
            if (note is null) return Results.NotFound();
            if (note.IsDeleted)
                return Results.Conflict(new { error = "deleted", purgeAt = note.PurgeAt });

            var doc = await db.Documents.FindAsync([note.DocumentId], ct);
            if (doc is null) return Results.NotFound();

            if (version != doc.Version)
                return Results.Conflict(new
                {
                    error = "version_conflict",
                    serverVersion = doc.Version,
                    serverUpdatedAt = doc.UpdatedAt,
                });

            var target = req.VaultPath.Trim();
            if (note.VaultPath == target)
                // 冪等（同じ名前への move は何も書かない。自分自身と衝突させない）。
                return Results.Ok(new MoveNoteResponse(note.DocumentId, note.VaultPath,
                    doc.Version, note.UpdatedAt));

            // パスの一意性は新規作成と**同じ関数**で判定する（数え方を 2 つ持たない。[[IADR-0353]] 決定 3）。
            if (await PrivateNoteEndpoints.ActivePathExistsAsync(db, owner, target, ct))
                return PrivateNoteEndpoints.PathConflictProblem(target);

            note.MoveTo(target, now);
            device.TouchSync(now);
            await db.SaveChangesAsync(ct);

            // ADR-0037 決定 9: 「誰が・いつ・何件」。パス（＝実質的な題名）は書かない。
            audit.Record("private-note.sync.move", owner, "granted", $"device={device.Id} count=1");
            return Results.Ok(new MoveNoteResponse(note.DocumentId, note.VaultPath, doc.Version,
                note.UpdatedAt));
        });
    }
}
