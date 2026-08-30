using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Features.PrivateNotes.Create;

// FR-19, SC-19: 作成（本文なし。本文編集は Obsidian 経路に限る — ADR-0046 D-03）。
// ADR-0037 決定 17: 100% 到達時は**新規作成のみ**拒否する。
internal static class CreatePrivateNoteEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/", async (CreatePrivateNoteRequest req, HttpContext http,
            DocumentDbContext db, CancellationToken ct) =>
        {
            if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["title"] = ["タイトルは必須です。"]
                });

            var now = DateTimeOffset.UtcNow;
            var used = await PrivateNoteUsage.UsedBytesAsync(db, owner, ct);
            var quota = await PrivateNoteUsage.GetOrCreateQuotaAsync(db, owner, now, ct);
            if (quota.RejectsNewNote(used, 0))
                return PrivateNoteEndpoints.QuotaExceededProblem(used, quota.LimitBytes);

            var vaultPath = string.IsNullOrWhiteSpace(req.VaultPath)
                ? $"{req.Title.Trim()}.md"
                : req.VaultPath.Trim();
            if (await PrivateNoteEndpoints.ActivePathExistsAsync(db, owner, vaultPath, ct))
                return PrivateNoteEndpoints.PathConflictProblem(vaultPath);

            // FR-19 受け入れ基準: 公開範囲＝非公開（共有 0 件）・機密区分 restricted・3 トグル OFF で作成。
            var doc = Document.Create(req.Title.Trim(), originalUri: null,
                contentType: DocumentBodyIntake.ContentType,
                attributes: PrivateNoteEndpoints.PrivateNoteDefaults(owner), tags: []);
            db.Documents.Add(doc);
            var note = PrivateNote.Create(doc.Id, owner, vaultPath, latestBytes: 0,
                contentHash: null, now);
            db.PrivateNotes.Add(note);
            // 本文なし（0 バイト）の作成は使用量を変えないため、警告の再評価は不要である。
            await db.SaveChangesAsync(ct);
            return Results.Created($"/private-notes/{doc.Id}", PrivateNoteEndpoints.ToDto(note, doc));
        });
    }
}
