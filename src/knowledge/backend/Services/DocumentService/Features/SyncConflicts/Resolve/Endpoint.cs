using System.Text;
using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Features.PrivateNotes;
using DocumentService.Infrastructure.Persistence;
using FluentValidation;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Audit;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace DocumentService.Features.SyncConflicts.Resolve;

// FR-20, UC-11, SC-20 主要素 5, ADR-0037 決定 7・17, [[IADR-0352]], #1442: 競合の解決（3 択）。
//
// - `local`: 端末の本文を**新しい版**として資料へ書く（サーバ版を土台に 1 版進む）。
// - `server`: サーバ版をそのまま採る。**資料は 1 バイトも変わらない**（版も進めない）。
// - `both`: サーバ版はそのまま、端末の本文を**別名の新規資料**として作る。
//   🔴 **新規作成なので容量上限が効く**（100% 到達時は 507。ADR-0037 決定 17）——
//   「両方を残す」を容量の例外にすると、上限が事実上無くなる。
//
// **自動解決は無い。** どの分岐も利用者が選んだ結果の適用であり、サーバが選ぶ経路は存在しない。
//
// 🔴 **ローカル本文の削除は SaveChanges の前**である（IADR-0296 決定 3 と同じ向き）——
// 先に解決済みにしてから削除に失敗すると、**解決済みなのに本文だけが残り、誰も消せない**。
// 前に置けば、失敗したときは何も確定せず利用者が選び直せる。
internal static class ResolveSyncConflictEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/{id:guid}/resolve", async (Guid id, ResolveSyncConflictRequest req,
            IValidator<ResolveSyncConflictRequest> validator, HttpContext http,
            DocumentDbContext db, IObjectStorageClient storage, IPrivateNoteNotifier notifier,
            IAuditLogger audit, CancellationToken ct) =>
        {
            if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner) return Results.Unauthorized();

            // 🔴 **401 の後ろ・DB 照会の前**（他の端点と同じ順。前へ出すと 404 が 400 に化ける）。
            var gate = validator.Validate(req);
            if (!gate.IsValid) return ValidationProblems.FirstViolation(gate);

            var conflict = await SyncConflictEndpoints.FindOwnedAsync(db, owner, id, ct);
            if (conflict is null) return Results.NotFound();
            // 解決済みへの再解決は 409 —— 自分の競合であることは判っており、黙って 2 度目を
            // 適用すると版がもう 1 つ進む（`local` で顕著）。
            if (conflict.IsResolved)
                return Results.Conflict(new
                {
                    error = "already_resolved",
                    resolution = conflict.Resolution,
                    resolvedAt = conflict.ResolvedAt,
                });

            var note = await db.PrivateNotes.FindAsync([conflict.DocumentId], ct);
            var doc = await db.Documents.FindAsync([conflict.DocumentId], ct);
            if (note is null || doc is null) return Results.NotFound();

            var now = DateTimeOffset.UtcNow;
            var localContent = await Get.GetSyncConflictEndpoint.ReadAsync(storage,
                conflict.LocalContentUri, ct);
            Guid? createdNoteId = null;

            if (req.Resolution == SyncConflictResolutions.Local)
            {
                await storage.PutTextAsync(DocumentBodyIntake.StorageKey(doc.Id), localContent,
                    DocumentBodyIntake.ContentType, ct);
                doc.RecordContentFingerprint(DocumentBodyIntake.Fingerprint(localContent));
                doc.Update(doc.Title, doc.Attributes, doc.Tags.ToList(), "conflict-resolve-local");
                note.RecordBody(Encoding.UTF8.GetByteCount(localContent),
                    DocumentBodyIntake.Fingerprint(localContent), now);
            }
            else if (req.Resolution == SyncConflictResolutions.Both)
            {
                var created = await CreateAliasNoteAsync(db, storage, owner, note, doc,
                    localContent, now, ct);
                if (created.Problem is not null) return created.Problem;
                createdNoteId = created.NoteId;
            }
            // `server`: 何もしない（資料はサーバ版のまま。版も進まない）。

            if (!string.IsNullOrEmpty(conflict.LocalContentUri))
                await storage.DeleteAsync(conflict.LocalContentUri, ct);
            conflict.Resolve(req.Resolution, now);
            await db.SaveChangesAsync(ct);

            // FR-22 ②: `both` で使用量が増えるため、警告の跨ぎを再評価する。
            await PrivateNoteUsage.RecordUsageAndWarnAsync(db, notifier, owner, now, ct);

            // ADR-0037 決定 9: 監査は「誰が・いつ・何件」。**タイトル・本文は記録しない。**
            audit.Record("private-note.sync.conflict-resolve", owner, "granted",
                $"conflict={conflict.Id} resolution={req.Resolution} count=1");

            return Results.Ok(new ResolveSyncConflictResponse(conflict.Id, conflict.DocumentId,
                req.Resolution, doc.Version, createdNoteId));
        });
    }

    // SC-20 主要素 5: `both` の別名資料。**新規作成の規則（容量上限・経路衝突）を通す** ——
    // 既存の作成経路（`PrivateNoteEndpoints` の 507 / 409）と同じ応答であり、別の規則を作らない。
    private static async Task<(Guid? NoteId, IResult? Problem)> CreateAliasNoteAsync(
        DocumentDbContext db, IObjectStorageClient storage, string owner, PrivateNote source,
        Document sourceDoc, string localContent, DateTimeOffset now, CancellationToken ct)
    {
        var bytes = (long)Encoding.UTF8.GetByteCount(localContent);

        // ADR-0037 決定 17: 100% 到達時（および上限を跨ぐ場合）は**新規作成を拒否**する。
        var used = await PrivateNoteUsage.UsedBytesAsync(db, owner, ct);
        var quota = await PrivateNoteUsage.GetOrCreateQuotaAsync(db, owner, now, ct);
        if (quota.RejectsNewNote(used, bytes))
            return (null, PrivateNoteEndpoints.QuotaExceededProblem(used, quota.LimitBytes));

        var vaultPath = ConflictAlias.PathOf(source.VaultPath, now);
        if (await PrivateNoteEndpoints.ActivePathExistsAsync(db, owner, vaultPath, ct))
            return (null, PrivateNoteEndpoints.PathConflictProblem(vaultPath));

        var id = Guid.NewGuid();
        var uri = await storage.PutTextAsync(DocumentBodyIntake.StorageKey(id), localContent,
            DocumentBodyIntake.ContentType, ct);
        // 既定（doc_scope=private-note / owner / restricted / 露出 3 トグル OFF）は作成経路と同じ。
        // **露出はすべて OFF で作られる**ため、索引の生産側へは何も発行しない（ADR-0061 決定 2）。
        var doc = Document.CreateWithBody(id, ConflictAlias.TitleOf(sourceDoc.Title, now), uri,
            originalUri: null, contentType: DocumentBodyIntake.ContentType,
            attributes: PrivateNoteEndpoints.PrivateNoteDefaults(owner), tags: [],
            contentFingerprint: DocumentBodyIntake.Fingerprint(localContent));
        db.Documents.Add(doc);
        db.PrivateNotes.Add(PrivateNote.Create(id, owner, vaultPath, bytes,
            DocumentBodyIntake.Fingerprint(localContent), now));
        return (id, null);
    }
}
