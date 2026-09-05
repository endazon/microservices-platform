using System.Text;
using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Features.Documents;
using DocumentService.Features.PrivateNotes;
using DocumentService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Audit;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace DocumentService.Features.ObsidianSync.Push;

// FR-20, ADR-0037 決定 2・8: push（新規作成・更新）。
// **1 編集 = 1 版**。オフラインで 10 回編集して 1 回同期した場合も、edits に 10 要素を
// 載せれば 10 版として刻まれる（決定 8）。
//
// **［#1184］本文の書き込みは「露出 3 トグルのうち 1 つでも ON」のときだけ `DocumentUpdated` を
// 発行する**（ADR-0061 決定 1・2 / [[IADR-0394]] 決定 4。門は
// `DocumentEndpoints.PublishUpdatedIfIndexableAsync` 1 か所）。**新規作成は必ず 3 つとも OFF
// である**（`PrivateNoteDefaults`）ため、初回 push は何も発行しない —— 索引に存在しない状態を
// 既定として構造的に守る（[[IADR-0270]] 決定 5 が守っていた性質は、門の形で残る）。
internal static class PushNoteEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/notes", async (PushNoteRequest req, HttpContext http, DocumentDbContext db,
            IObjectStorageClient storage, IPrivateNoteNotifier notifier, IAuditLogger audit,
            IDocumentUpdatedPublisher bus, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var device = await ObsidianSyncEndpoints.ResolveDeviceAsync(http, db, now, ct);
            if (device is null) return Results.Unauthorized();
            var owner = device.OwnerId;

            if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.VaultPath)
                || req.Edits is not { Count: > 0 } || req.Edits.Any(e => e.Content is null))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["errors"] = ["title / vaultPath / edits（1 件以上・content 必須）を指定してください。"]
                });

            // FR-21 と同じ上限（1 MB / 413）。同期経路だけ上限が違うと
            // 「Obsidian では書けるが KB に入らない」資料ができる（[[IADR-0270]] 決定 7）。
            if (req.Edits.Any(e => DocumentBodyIntake.ExceedsLimit(e.Content!)))
                return Results.Problem(
                    title: "本文が上限を超えています。",
                    detail: $"本文の上限は {DocumentBodyIntake.MaxBytes} バイト（UTF-8）です。"
                          + "上限を超える本文は切り詰めずに拒否します。",
                    statusCode: StatusCodes.Status413PayloadTooLarge);

            var lastContent = req.Edits[^1].Content!;
            var lastBytes = (long)Encoding.UTF8.GetByteCount(lastContent);
            var lastHash = ContentHashOf(lastContent);

            if (req.NoteId is null)
            {
                // ── 新規作成（Obsidian 側で作られたファイルの初回同期） ──
                // ADR-0037 決定 17: 100% 到達時（および上限を跨ぐ場合）は新規作成を拒否する。
                var used = await PrivateNoteUsage.UsedBytesAsync(db, owner, ct);
                var quota = await PrivateNoteUsage.GetOrCreateQuotaAsync(db, owner, now, ct);
                if (quota.RejectsNewNote(used, lastBytes))
                    return PrivateNoteEndpoints.QuotaExceededProblem(used, quota.LimitBytes);

                if (await PrivateNoteEndpoints.ActivePathExistsAsync(db, owner,
                        req.VaultPath.Trim(), ct))
                    return PrivateNoteEndpoints.PathConflictProblem(req.VaultPath.Trim());

                var id = Guid.NewGuid();
                // ADR-0037 フォローアップ 8: プラグイン流入は画面バリデーションを経由しないため、
                // フェイルセーフ既定（restricted / doc_scope=private-note / owner）をサーバ側で適用する。
                var firstUri = await storage.PutTextAsync(DocumentBodyIntake.StorageKey(id),
                    req.Edits[0].Content!, DocumentBodyIntake.ContentType, ct);
                var doc = Document.CreateWithBody(id, req.Title.Trim(), firstUri,
                    originalUri: null, contentType: DocumentBodyIntake.ContentType,
                    attributes: PrivateNoteEndpoints.PrivateNoteDefaults(owner), tags: [],
                    // ADR-0050 (#911): 本文指紋（ContentHash と同じ計算）。
                    contentFingerprint: DocumentBodyIntake.Fingerprint(req.Edits[0].Content!));
                db.Documents.Add(doc);
                await ApplyEditsAsync(doc, req, storage, skipFirst: true, ct);

                var note = PrivateNote.Create(id, owner, req.VaultPath.Trim(), lastBytes,
                    lastHash, now);
                db.PrivateNotes.Add(note);
                device.TouchSync(now);
                await db.SaveChangesAsync(ct);
                await PrivateNoteUsage.RecordUsageAndWarnAsync(db, notifier, owner, now, ct);
                await db.SaveChangesAsync(ct);

                // ADR-0037 決定 9: 監査は「誰が・いつ・何件」。タイトル・内容は記録しない。
                await PublishIfExposedAsync(bus, db, doc, ct);

                audit.Record("private-note.sync.push", owner, "granted",
                    $"device={device.Id} count=1 versions={req.Edits.Count}");
                return Results.Created($"/private-notes/sync/notes/{id}",
                    new PushNoteResponse(id, doc.Version, lastHash, lastBytes));
            }
            else
            {
                // ── 既存資料の更新 ──
                var note = await ObsidianSyncEndpoints.FindOwnedAsync(db, owner, req.NoteId.Value, ct);
                if (note is null) return Results.NotFound();
                if (note.IsDeleted)
                    return Results.Conflict(new { error = "deleted", purgeAt = note.PurgeAt });

                var doc = await db.Documents.FindAsync([note.DocumentId], ct);
                if (doc is null) return Results.NotFound();

                // ADR-0037 決定 7: 競合はサーバで解決しない。クライアントが最後に見た版
                // （baseVersion）と現在版の不一致を 409 で返し、選択は利用者に委ねる。
                if (req.BaseVersion is not { } baseVersion)
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["baseVersion"] = ["既存資料の更新には baseVersion が必須です。"]
                    });
                if (baseVersion != doc.Version)
                    return Results.Conflict(new
                    {
                        error = "version_conflict",
                        serverVersion = doc.Version,
                        serverUpdatedAt = doc.UpdatedAt,
                    });

                // ADR-0037 決定 17: **更新は容量を見ずに通す**（100% でも保存できる。
                // 書きかけを失わせない）。超過分は最新版の増分に限られる。
                await ApplyEditsAsync(doc, req, storage, skipFirst: false, ct);
                note.RecordBody(lastBytes, lastHash, now);
                device.TouchSync(now);
                await db.SaveChangesAsync(ct);
                await PrivateNoteUsage.RecordUsageAndWarnAsync(db, notifier, owner, now, ct);
                await db.SaveChangesAsync(ct);

                await PublishIfExposedAsync(bus, db, doc, ct);

                audit.Record("private-note.sync.push", owner, "granted",
                    $"device={device.Id} count=1 versions={req.Edits.Count}");
                return Results.Ok(new PushNoteResponse(doc.Id, doc.Version, lastHash, lastBytes));
            }
        });
    }

    // ADR-0037 決定 8: edits を時系列順に 1 編集 = 1 版として適用する。本文は正準キー
    // （documents/{id}/body.md）へ都度格納する。**キーは文書 ID で固定なので、後の編集が前の編集を
    // 上書きする —— 版ごとの本文は残らない**（#1011 / [[IADR-0290]]。バケットのバージョニングは
    // 参照 URI に versionId を持たないため、有効でも過去の本文は引けない）。
    // 版の復元は FR-06 の射程外であり（計画 FR-06［2026-08-23 明確化］・環流 planning#473）、
    // ここで残すべき本文は**最新の 1 つ**である。
    private static async Task ApplyEditsAsync(Document doc, PushNoteRequest req,
        IObjectStorageClient storage, bool skipFirst, CancellationToken ct)
    {
        var edits = skipFirst ? req.Edits.Skip(1) : req.Edits;
        foreach (var edit in edits)
        {
            await storage.PutTextAsync(DocumentBodyIntake.StorageKey(doc.Id), edit.Content!,
                DocumentBodyIntake.ContentType, ct);
            // ADR-0050 (#911): 版適用のたびに本文指紋を進める（最新版の指紋が正）。
            doc.RecordContentFingerprint(DocumentBodyIntake.Fingerprint(edit.Content!));
            doc.Update(req.Title.Trim(), doc.Attributes, doc.Tags.ToList(),
                edit.ChangeNote ?? "sync-edit");
        }
    }

    // FR-19, ADR-0061 決定 1・2 / [[IADR-0394]] 決定 4 (#1184): 本文の書き込みを索引の生産側へ流す門。
    // **判定は `DocumentExposure.IsIndexable`（唯一の述語）**であり、条件を書き下さない。
    private static async Task PublishIfExposedAsync(IDocumentUpdatedPublisher bus,
        DocumentDbContext db, Document doc, CancellationToken ct)
    {
        var names = await TagResolver.NamesAsync(db, ct);
        await DocumentEndpoints.PublishUpdatedIfIndexableAsync(bus, db, doc, names, ct);
    }

    // ADR-0050 (#911): 計算の実体は DocumentBodyIntake.Fingerprint（1 か所に集める）。
    private static string ContentHashOf(string content)
        => DocumentBodyIntake.Fingerprint(content);
}
