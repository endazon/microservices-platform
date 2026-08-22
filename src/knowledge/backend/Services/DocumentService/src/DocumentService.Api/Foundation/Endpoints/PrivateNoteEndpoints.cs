using DocumentService.Api.Foundation.Domain;
using DocumentService.Api.Foundation.Persistence;
using DocumentService.Api.Foundation.Ports;
using DocumentService.Api.Foundation.Services;
using Knowledge.Contracts.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Audit;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Api.Foundation.Endpoints;

// FR-19, UC-11, SC-19, ADR-0037 決定 5・16・17・19・20, ADR-0054 決定 4, [[IADR-0270]]:
// 個人資料（private-note）のライフサイクル端点。一覧＋容量表示・作成・論理削除・復元・
// 完全削除（単票／一括）・露出 3 トグル・管理者による上限変更。
//
// **主体はトークンからしか採らない**（クエリ・本文に主体の口を作らない）。
// 所有者スコープは台帳（PrivateNote.OwnerId）で判定し、他者の資料は**存在ごと秘匿**する
// （404。403 を返すと他人の資料 ID の実在が漏れる。ADR-0036 D-04 の存在秘匿と同じ向き）。
//
// 🔴 本経路は DocumentUpdated を**発行しない**（[[IADR-0270]] 決定 5）—— 露出 3 トグルの既定 OFF を
// 「索引に存在しない」ことで構造的に守る。完全削除だけは DocumentDeleted を発行する（下流掃除の向き）。
public static class PrivateNoteEndpoints
{
    public static IEndpointRouteBuilder MapPrivateNoteEndpoints(this IEndpointRouteBuilder app)
    {
        // FR-19: 一般利用者の操作。認証は必須・ロールは要求しない（管理者限定にすると所有者が使えない）。
        var g = app.MapGroup("/private-notes").WithTags("PrivateNotes").RequireAuthorization();

        // SC-19: 管理者の上限変更（FR-19「管理者が最大 1 TB まで引き上げられる」）。
        var admin = app.MapGroup("/private-notes/quotas").WithTags("PrivateNotes")
            .RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        // FR-19, SC-19: 一覧（削除済みを含む）＋容量表示。
        // 削除済み行の bytes は「完全削除で解放される容量」の表示にそのまま使える（ADR-0037 決定 20）。
        g.MapGet("/", async (HttpContext http, DocumentDbContext db, CancellationToken ct) =>
        {
            if (SubjectOf(http) is not { } owner) return Results.Unauthorized();

            var now = DateTimeOffset.UtcNow;
            var notes = await db.PrivateNotes.Where(n => n.OwnerId == owner)
                .OrderByDescending(n => n.UpdatedAt).ToListAsync(ct);
            var docIds = notes.Select(n => n.DocumentId).ToList();
            var docs = await db.Documents.Where(d => docIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, ct);

            var used = await PrivateNoteUsage.UsedBytesAsync(db, owner, ct);
            var quota = await PrivateNoteUsage.GetOrCreateQuotaAsync(db, owner, now, ct);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new PrivateNoteListResponse(
                new PrivateNoteUsageDto(used, quota.LimitBytes, quota.PercentOf(used)),
                notes.Select(n => ToDto(n, docs.GetValueOrDefault(n.DocumentId))).ToList()));
        });

        // FR-19, SC-19: 作成（本文なし。本文編集は Obsidian 経路に限る — ADR-0046 D-03）。
        // ADR-0037 決定 17: 100% 到達時は**新規作成のみ**拒否する。
        g.MapPost("/", async (CreatePrivateNoteRequest req, HttpContext http,
            DocumentDbContext db, CancellationToken ct) =>
        {
            if (SubjectOf(http) is not { } owner) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["title"] = ["タイトルは必須です。"]
                });

            var now = DateTimeOffset.UtcNow;
            var used = await PrivateNoteUsage.UsedBytesAsync(db, owner, ct);
            var quota = await PrivateNoteUsage.GetOrCreateQuotaAsync(db, owner, now, ct);
            if (quota.RejectsNewNote(used, 0))
                return QuotaExceededProblem(used, quota.LimitBytes);

            var vaultPath = string.IsNullOrWhiteSpace(req.VaultPath)
                ? $"{req.Title.Trim()}.md"
                : req.VaultPath.Trim();
            if (await ActivePathExistsAsync(db, owner, vaultPath, ct))
                return PathConflictProblem(vaultPath);

            // FR-19 受け入れ基準: 公開範囲＝非公開（共有 0 件）・機密区分 restricted・3 トグル OFF で作成。
            var doc = Document.Create(req.Title.Trim(), originalUri: null,
                contentType: DocumentBodyIntake.ContentType,
                attributes: PrivateNoteDefaults(owner), tags: []);
            db.Documents.Add(doc);
            var note = PrivateNote.Create(doc.Id, owner, vaultPath, latestBytes: 0,
                contentHash: null, now);
            db.PrivateNotes.Add(note);
            // 本文なし（0 バイト）の作成は使用量を変えないため、警告の再評価は不要である。
            await db.SaveChangesAsync(ct);
            return Results.Created($"/private-notes/{doc.Id}", ToDto(note, doc));
        });

        // FR-19, ADR-0037 決定 5・19: 論理削除（90 日間は復元可）。**容量は空かない**
        // （capacityFreed=false を応答で明示し、SC-19 の確認文言の根拠にする。決定 20）。
        g.MapDelete("/{id:guid}", async (Guid id, HttpContext http, DocumentDbContext db,
            CancellationToken ct) =>
        {
            if (SubjectOf(http) is not { } owner) return Results.Unauthorized();
            var note = await FindOwnedAsync(db, owner, id, ct);
            if (note is null) return Results.NotFound();

            note.SoftDelete(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new
            {
                deletedAt = note.DeletedAt,
                purgeAt = note.PurgeAt,
                // 決定 19・20: 論理削除しても容量は空かない（利用者へ伝える事実の機械可読な形）。
                capacityFreed = false,
            });
        });

        // FR-19: 復元（90 日以内。purge 済みは行が無く 404 になる＝復元不可）。
        g.MapPost("/{id:guid}/restore", async (Guid id, HttpContext http, DocumentDbContext db,
            CancellationToken ct) =>
        {
            if (SubjectOf(http) is not { } owner) return Results.Unauthorized();
            var note = await FindOwnedAsync(db, owner, id, ct);
            if (note is null) return Results.NotFound();
            if (!note.IsDeleted)
                return Results.Conflict(new { error = "not_deleted" });

            note.Restore(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(ct);
            var doc = await db.Documents.FindAsync([id], ct);
            return Results.Ok(ToDto(note, doc));
        });

        // FR-19, ADR-0037 決定 20: 完全削除（即時・復元不可）。単票も一括も本端点（ids の要素数の差）。
        // 対象は**削除済みのみ**（SC-19 の削除済み一覧からの操作）。解放される容量を応答で返す。
        g.MapPost("/purge", async (PurgePrivateNotesRequest req, HttpContext http,
            DocumentDbContext db, IPrivateNoteNotifier notifier, IPublishEndpoint bus,
            IAuditLogger audit, CancellationToken ct) =>
        {
            if (SubjectOf(http) is not { } owner) return Results.Unauthorized();
            if (req.Ids is not { Count: > 0 })
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["ids"] = ["完全削除する資料の ID を 1 件以上指定してください。"]
                });

            var ids = req.Ids.Distinct().ToList();
            var notes = await db.PrivateNotes
                .Where(n => n.OwnerId == owner && ids.Contains(n.DocumentId))
                .ToListAsync(ct);
            // 他者の資料・存在しない ID は区別せず 404（存在秘匿）。
            if (notes.Count != ids.Count) return Results.NotFound();
            if (notes.Any(n => !n.IsDeleted))
                return Results.Conflict(new { error = "not_deleted" });

            var now = DateTimeOffset.UtcNow;
            var freedBytes = notes.Sum(n => n.LatestBytes);
            var docs = await db.Documents.Where(d => ids.Contains(d.Id)).ToListAsync(ct);
            db.Documents.RemoveRange(docs);           // 版・共有・台帳はカスケード削除
            db.PrivateNotes.RemoveRange(notes);       // InMemory はカスケードしないため明示にも消す
            await db.SaveChangesAsync(ct);
            // 削除を確定させてから使用量を再計算する（先に計算すると purge 分が残って見える）。
            // 使用量が下がって閾値を割れば、警告の発火記録がここで再武装される（FR-22 ②）。
            await PrivateNoteUsage.RecordUsageAndWarnAsync(db, notifier, owner, now, ct);
            await db.SaveChangesAsync(ct);

            // ADR-0037 決定 9・11-①: 監査は「誰が・いつ・何件」。タイトルは記録しない。
            audit.Record("private-note.purge", owner, "granted", $"count={notes.Count}");
            foreach (var id in ids)
                await bus.Publish(new DocumentDeleted(id, now), ct);

            return Results.Ok(new PurgePrivateNotesResponse(notes.Count, freedBytes));
        });

        // FR-19, SC-20: 露出 3 トグル（横断検索／グラフ／AI 入力）。既定 OFF・独立に設定できる。
        // 🔴 ON の消費側配線は IADR-0253 段 3 の完了待ちであり、本段では保存のみ（[[IADR-0270]] 決定 5）。
        g.MapPut("/{id:guid}/exposure", async (Guid id, UpdateExposureRequest req,
            HttpContext http, DocumentDbContext db, CancellationToken ct) =>
        {
            if (SubjectOf(http) is not { } owner) return Results.Unauthorized();
            var note = await FindOwnedAsync(db, owner, id, ct);
            if (note is null) return Results.NotFound();

            note.SetExposure(req.IncludeInSearch, req.IncludeInGraph, req.IncludeInAi,
                DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(ct);
            var doc = await db.Documents.FindAsync([id], ct);
            return Results.Ok(ToDto(note, doc));
        });

        // FR-19, NFR-27: 管理者による上限の照会・変更（既定 1 GB・最大 1 TB）。
        admin.MapGet("/{ownerId}", async (string ownerId, DocumentDbContext db,
            CancellationToken ct) =>
        {
            var used = await PrivateNoteUsage.UsedBytesAsync(db, ownerId, ct);
            var quota = await db.PrivateNoteQuotas.FindAsync([ownerId], ct);
            var limit = quota?.LimitBytes ?? PrivateNoteQuota.DefaultLimitBytes;
            return Results.Ok(new PrivateNoteUsageDto(used, limit,
                limit <= 0 ? 100 : (int)(used * 100 / limit)));
        });

        admin.MapPut("/{ownerId}", async (string ownerId, SetQuotaRequest req,
            DocumentDbContext db, IAuditLogger audit, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var quota = await PrivateNoteUsage.GetOrCreateQuotaAsync(db, ownerId, now, ct);
            try
            {
                quota.SetLimit(req.LimitBytes, now);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["limitBytes"] = [ex.Message]
                });
            }
            await db.SaveChangesAsync(ct);
            audit.Record("private-note.quota.set", ownerId, "granted",
                $"limitBytes={req.LimitBytes}");
            var used = await PrivateNoteUsage.UsedBytesAsync(db, ownerId, ct);
            return Results.Ok(new PrivateNoteUsageDto(used, quota.LimitBytes,
                quota.PercentOf(used)));
        });

        return app;
    }

    // FR-19 受け入れ基準: 新規作成時の既定値。doc_scope=private-note（ADR-0054）・owner=本人
    // （ADR-0036 D-05）・機密区分 restricted（07_abac-attribute-model のフェイルセーフ既定）。
    internal static Dictionary<string, string> PrivateNoteDefaults(string owner) => new()
    {
        [DocumentAttributes.DocScopeKey] = DocumentAttributes.DocScopePrivateNote,
        [DocumentBodyIntake.OwnerKey] = owner,
        [DocumentAttributes.ConfidentialityKey] = "restricted",
    };

    internal static string? SubjectOf(HttpContext http)
        => string.IsNullOrWhiteSpace(http.User.Identity?.Name) ? null : http.User.Identity!.Name;

    private static async Task<PrivateNote?> FindOwnedAsync(DocumentDbContext db, string owner,
        Guid id, CancellationToken ct)
    {
        var note = await db.PrivateNotes.FindAsync([id], ct);
        return note is not null && note.OwnerId == owner ? note : null;
    }

    internal static async Task<bool> ActivePathExistsAsync(DocumentDbContext db, string owner,
        string vaultPath, CancellationToken ct)
        => await db.PrivateNotes.AnyAsync(
            n => n.OwnerId == owner && n.VaultPath == vaultPath && n.DeletedAt == null, ct);

    internal static PrivateNoteDto ToDto(PrivateNote n, Document? doc) => new(
        n.DocumentId,
        doc?.Title ?? string.Empty,
        n.VaultPath,
        doc?.Version ?? 0,
        n.LatestBytes,
        n.ContentHash,
        n.IncludeInSearch,
        n.IncludeInGraph,
        n.IncludeInAi,
        n.IsDeleted,
        n.DeletedAt,
        n.PurgeAt,
        n.CreatedAt,
        n.UpdatedAt);

    // ADR-0037 決定 17: 100% 到達時の新規作成拒否。507 Insufficient Storage（WebDAV 由来の
    // 容量超過の標準コード）。**更新はこの拒否を通らない**ことが決定の要である。
    internal static IResult QuotaExceededProblem(long usedBytes, long limitBytes) => Results.Problem(
        title: "保存容量の上限に達しています。",
        detail: $"使用量 {usedBytes} バイト / 上限 {limitBytes} バイト。新規作成はできません。"
              + "削除済み資料の完全削除で容量を空けるか、管理者に上限の引き上げを依頼してください"
              + "（論理削除では容量は空きません）。",
        statusCode: StatusCodes.Status507InsufficientStorage);

    internal static IResult PathConflictProblem(string vaultPath) => Results.Conflict(new
    {
        error = "vault_path_conflict",
        vaultPath,
    });
}

public record CreatePrivateNoteRequest(string Title, string? VaultPath = null);

public record PurgePrivateNotesRequest(List<Guid> Ids);

public record PurgePrivateNotesResponse(int PurgedCount, long FreedBytes);

public record UpdateExposureRequest(bool IncludeInSearch, bool IncludeInGraph, bool IncludeInAi);

public record SetQuotaRequest(long LimitBytes);

public record PrivateNoteUsageDto(long UsedBytes, long LimitBytes, int Percent);

public record PrivateNoteDto(
    Guid Id,
    string Title,
    string VaultPath,
    int Version,
    long Bytes,
    string? ContentHash,
    bool IncludeInSearch,
    bool IncludeInGraph,
    bool IncludeInAi,
    bool Deleted,
    DateTimeOffset? DeletedAt,
    DateTimeOffset? PurgeAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public record PrivateNoteListResponse(PrivateNoteUsageDto Usage, List<PrivateNoteDto> Notes);
