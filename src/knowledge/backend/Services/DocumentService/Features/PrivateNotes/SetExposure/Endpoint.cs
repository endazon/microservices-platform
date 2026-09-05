using DocumentService.Domain.Ports;
using DocumentService.Features.Documents;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Features.PrivateNotes.SetExposure;

// FR-19, SC-20, ADR-0061 決定 1〜4: 露出 3 トグル（横断検索／グラフ／AI 入力）。既定 OFF・独立に設定できる。
//
// **［#1184］本経路は `DocumentUpdated` を発行する**（[[IADR-0395]] 決定 4。
// [[IADR-0270]] 決定 5 の「発行しない」は本 ADR で解除された —— 旧 ID は残し、後継を併記する）。
// 発行の条件は 2 つだけである。
//
//   1. **3 トグルのうち 1 つでも ON**（決定 1）—— 索引へ載せるために出す。
//   2. **ON → 全 OFF へ戻った**（決定 4）—— **索引から消させる**ために出す。
//      🔴 **「属性で弾く」で済ませない。** 残った本文はフィルタの実装ミス 1 つで露出に変わる
//      （`ADR-0057` 決定 1・SC-19 の「いかなる方法でも復元できません」と同じ理由）。
//      受け手（`IngestionService` / `GraphService`）が**同じ述語**を評価して削除する。
//
// 全 OFF のまま全 OFF を保存した場合は**何も出さない** —— 索引に存在しない状態を、
// 存在しないまま保つ（決定 2 の「構造的に守る」）。
//
// 属性の更新は台帳と**同じトランザクションで**行う（台帳と属性が食い違わないため）。
internal static class SetPrivateNoteExposureEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPut("/{id:guid}/exposure", async (Guid id, UpdateExposureRequest req,
            HttpContext http, DocumentDbContext db, IDocumentUpdatedPublisher bus,
            CancellationToken ct) =>
        {
            if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner) return Results.Unauthorized();
            var note = await PrivateNoteEndpoints.FindOwnedAsync(db, owner, id, ct);
            if (note is null) return Results.NotFound();

            note.SetExposure(req.IncludeInSearch, req.IncludeInGraph, req.IncludeInAi,
                DateTimeOffset.UtcNow);
            var doc = await db.Documents.FindAsync([id], ct);

            // 🔴 **変更「前」の値で判定する。** 撤収（決定 4）は「以前は載っていた」ことが条件であり、
            // 属性を書き換えた後では常に偽になる。
            var wasIndexable = doc is not null && DocumentExposure.IsIndexable(doc.Attributes);

            // **版は進めない**（[[IADR-0283]] 決定 4）——露出トグルは本文の編集ではない。
            doc?.SetExposureAttributes(req.IncludeInSearch, req.IncludeInGraph, req.IncludeInAi);
            await db.SaveChangesAsync(ct);

            if (doc is not null && (wasIndexable || DocumentExposure.IsIndexable(doc.Attributes)))
            {
                var names = await TagResolver.NamesAsync(db, ct);
                await DocumentEndpoints.PublishUpdatedAsync(bus, db, doc, names, ct);
            }

            return Results.Ok(PrivateNoteEndpoints.ToDto(note, doc));
        });
    }
}
