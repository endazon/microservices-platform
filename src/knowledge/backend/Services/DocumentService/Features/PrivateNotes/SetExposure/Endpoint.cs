using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Features.PrivateNotes.SetExposure;

// FR-19, SC-20: 露出 3 トグル（横断検索／グラフ／AI 入力）。既定 OFF・独立に設定できる。
//
// 🔴 **本経路は依然として `DocumentUpdated` を発行しない**（[[IADR-0270]] 決定 5 を維持）。
// 「横断検索に含める」ON で索引へ流す**生産側**の配線は同決定のフォローアップ 2 に残る。
//
// **［#447］「AI の入力に含める」だけは ABAC 文書属性 `ai_input` へ写す**
// （FR-21 受け入れ基準 ⑨ / [[IADR-0283]] 決定 4）—— 消費側（RAG 経路）が読む値であり、
// 台帳と属性が食い違わないよう**同じトランザクションで**更新する。属性を先に正しくしておく
// ことは漏れる向きではない（索引に載っていない文書の属性が正しいだけである）。
internal static class SetPrivateNoteExposureEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPut("/{id:guid}/exposure", async (Guid id, UpdateExposureRequest req,
            HttpContext http, DocumentDbContext db, CancellationToken ct) =>
        {
            if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner) return Results.Unauthorized();
            var note = await PrivateNoteEndpoints.FindOwnedAsync(db, owner, id, ct);
            if (note is null) return Results.NotFound();

            note.SetExposure(req.IncludeInSearch, req.IncludeInGraph, req.IncludeInAi,
                DateTimeOffset.UtcNow);
            var doc = await db.Documents.FindAsync([id], ct);
            // **版は進めない**（[[IADR-0283]] 決定 4）——露出トグルは本文の編集ではない。
            doc?.SetAiInputExposure(req.IncludeInAi);
            await db.SaveChangesAsync(ct);
            return Results.Ok(PrivateNoteEndpoints.ToDto(note, doc));
        });
    }
}
