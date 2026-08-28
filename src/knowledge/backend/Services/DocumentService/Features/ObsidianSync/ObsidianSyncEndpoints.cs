using System.Security.Cryptography;
using System.Text;
using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using DocumentService.Domain.Ports;
using DocumentService.Features.PrivateNotes;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Audit;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace DocumentService.Features.ObsidianSync;

// FR-20, UC-11, ADR-0037 決定 2〜5・7〜9・14, ADR-0046 D-03, 08_data-egress-policy 例外規定,
// [[IADR-0270]] 決定 3・5・7: Obsidian プラグイン向けの双方向同期プロトコル。
//
// **認証はブラウザセッション（JWT）ではなく同期トークン（Bearer）である**（ADR-0037 課題 2:
// 別系統の資格情報）。検証失敗は欠落・不正・期限切れ・失効のいずれも**同じ 401**で返し、
// 理由と存在を漏らさない（deny-by-default）。
//
// **スコープはトークンの所有者の個人資料のみ**（FR-20 / egress 例外の許容条件 1）。
// 他者の資料（共有されたものを含む）・組織文書は、どの端点からも到達できない ——
// すべての照会が台帳（PrivateNote.OwnerId == 所有者）を通るため、構造的に閉じている。
//
// **KB が唯一の正である**（決定 14）。競合（baseVersion 不一致）は 409 で返し、サーバは
// 自動解決しない（決定 7。「ローカル採用／サーバ採用／両方残す」の選択はプラグインが利用者へ提示する）。
public static class ObsidianSyncEndpoints
{
    public static IEndpointRouteBuilder MapObsidianSyncEndpoints(this IEndpointRouteBuilder app)
    {
        // JWT の RequireAuthorization は付けない（同期トークンが本経路の資格情報である）。
        var g = app.MapGroup("/private-notes/sync").WithTags("PrivateNotesSync");

        // FR-20: マニフェスト（同期対象の一覧）。削除済みも deleted=true で返し、
        // サーバ側の削除をプラグインが検知できるようにする（決定 14: KB が正）。
        g.MapGet("/manifest", async (HttpContext http, DocumentDbContext db,
            CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var device = await ResolveDeviceAsync(http, db, now, ct);
            if (device is null) return Results.Unauthorized();

            var notes = await db.PrivateNotes.Where(n => n.OwnerId == device.OwnerId)
                .OrderBy(n => n.VaultPath).ToListAsync(ct);
            var docIds = notes.Select(n => n.DocumentId).ToList();
            var docs = await db.Documents.Where(d => docIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, ct);

            device.TouchSync(now);
            await db.SaveChangesAsync(ct);

            return Results.Ok(notes.Select(n => new SyncManifestEntry(
                n.DocumentId,
                docs.GetValueOrDefault(n.DocumentId)?.Title ?? string.Empty,
                n.VaultPath,
                docs.GetValueOrDefault(n.DocumentId)?.Version ?? 0,
                n.ContentHash,
                n.IsDeleted,
                n.UpdatedAt)).ToList());
        });

        // FR-20, ADR-0037 決定 2・8: push（新規作成・更新）。
        // **1 編集 = 1 版**。オフラインで 10 回編集して 1 回同期した場合も、edits に 10 要素を
        // 載せれば 10 版として刻まれる（決定 8）。
        g.MapPost("/notes", async (PushNoteRequest req, HttpContext http, DocumentDbContext db,
            IObjectStorageClient storage, IPrivateNoteNotifier notifier, IAuditLogger audit,
            CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var device = await ResolveDeviceAsync(http, db, now, ct);
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
                audit.Record("private-note.sync.push", owner, "granted",
                    $"device={device.Id} count=1 versions={req.Edits.Count}");
                return Results.Created($"/private-notes/sync/notes/{id}",
                    new PushNoteResponse(id, doc.Version, lastHash, lastBytes));
            }
            else
            {
                // ── 既存資料の更新 ──
                var note = await FindOwnedAsync(db, owner, req.NoteId.Value, ct);
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

                audit.Record("private-note.sync.push", owner, "granted",
                    $"device={device.Id} count=1 versions={req.Edits.Count}");
                return Results.Ok(new PushNoteResponse(doc.Id, doc.Version, lastHash, lastBytes));
            }
        });

        // FR-20: pull（本文の取得）。個人資料の本文が端末へ出る egress の実行点であり、
        // 実行記録を監査ログへ残す（許容条件 4。タイトル・内容は記録しない）。
        g.MapGet("/notes/{id:guid}", async (Guid id, HttpContext http, DocumentDbContext db,
            IObjectStorageClient storage, IAuditLogger audit, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var device = await ResolveDeviceAsync(http, db, now, ct);
            if (device is null) return Results.Unauthorized();

            var note = await FindOwnedAsync(db, device.OwnerId, id, ct);
            if (note is null) return Results.NotFound();
            var doc = await db.Documents.FindAsync([id], ct);
            if (doc is null) return Results.NotFound();

            var content = doc.MarkdownUri is null
                ? string.Empty
                : await storage.GetTextAsync(doc.MarkdownUri, ct);

            device.TouchSync(now);
            await db.SaveChangesAsync(ct);
            audit.Record("private-note.sync.pull", device.OwnerId, "granted",
                $"device={device.Id} count=1");
            return Results.Ok(new PullNoteResponse(note.DocumentId, doc.Title, note.VaultPath,
                doc.Version, note.ContentHash, note.IsDeleted, content));
        });

        // FR-20, ADR-0037 決定 5: Obsidian 側の削除はサーバ側で**論理削除**とする（90 日保管）。
        // 冪等（削除済みへの再削除は期限を延ばさない）。
        g.MapPost("/notes/{id:guid}/delete", async (Guid id, HttpContext http,
            DocumentDbContext db, IAuditLogger audit, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var device = await ResolveDeviceAsync(http, db, now, ct);
            if (device is null) return Results.Unauthorized();

            var note = await FindOwnedAsync(db, device.OwnerId, id, ct);
            if (note is null) return Results.NotFound();

            note.SoftDelete(now);
            device.TouchSync(now);
            await db.SaveChangesAsync(ct);
            audit.Record("private-note.sync.delete", device.OwnerId, "granted",
                $"device={device.Id} count=1");
            return Results.Ok(new { deletedAt = note.DeletedAt, purgeAt = note.PurgeAt });
        });

        return app;
    }

    // [[IADR-0270]] 決定 3: Bearer 同期トークン → ハッシュ照合 → 有効（未失効・期限内）な端末。
    // 欠落・不正・期限切れ・失効はいずれも null（呼び出し側で同じ 401 になる）。
    private static async Task<SyncDevice?> ResolveDeviceAsync(HttpContext http,
        DocumentDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var auth = http.Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = auth["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(token)) return null;

        var hash = SyncTokens.HashOf(token);
        var device = await db.SyncDevices.FirstOrDefaultAsync(d => d.TokenHash == hash, ct);
        return device is not null && device.IsActive(now) ? device : null;
    }

    private static async Task<PrivateNote?> FindOwnedAsync(DocumentDbContext db, string owner,
        Guid id, CancellationToken ct)
    {
        var note = await db.PrivateNotes.FindAsync([id], ct);
        return note is not null && note.OwnerId == owner ? note : null;
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

    // ADR-0050 (#911): 計算の実体は DocumentBodyIntake.Fingerprint（1 か所に集める）。
    private static string ContentHashOf(string content)
        => DocumentBodyIntake.Fingerprint(content);
}

public record SyncEditRequest(string? Content, DateTimeOffset? EditedAt = null,
    string? ChangeNote = null);

public record PushNoteRequest(
    Guid? NoteId,
    string VaultPath,
    string Title,
    int? BaseVersion,
    List<SyncEditRequest> Edits);

public record PushNoteResponse(Guid NoteId, int Version, string ContentHash, long Bytes);

public record SyncManifestEntry(Guid NoteId, string Title, string VaultPath, int Version,
    string? ContentHash, bool Deleted, DateTimeOffset UpdatedAt);

public record PullNoteResponse(Guid NoteId, string Title, string VaultPath, int Version,
    string? ContentHash, bool Deleted, string Content);
