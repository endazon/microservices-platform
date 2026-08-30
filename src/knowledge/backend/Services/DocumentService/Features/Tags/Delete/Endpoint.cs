using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.Tags.Delete;

// FR-09, SC-09, #635: 削除する。**使用件数が 0 件のときだけ許す**
//（SC-09「参照が 1 件でもあるタグは削除拒否」。[[IADR-0153]] 決定 6）。
//
// **件数を添えて 409 を返す**——SC-09 が「削除前に使用件数を示す」と定めており、
// 数だけでも「まず 3 件を外してから消す」と管理者が行動できる。
internal static class DeleteTagEndpoint
{
    internal static void Map(RouteGroupBuilder write)
    {
        write.MapDelete("/{id:guid}", async (Guid id, DocumentDbContext db, CancellationToken ct) =>
        {
            var tag = await db.Tags.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (tag is null) return Results.NotFound();

            // **数え方は一覧と同じでなければならない**（`ListTagsEndpoint.LoadWithUsageAsync` と同じ母集合）。
            // 一覧が 0 件と表示したのに削除が 409 を返す（あるいはその逆）と、管理者は辞書を信用できなくなる。
            var documentTagLists = await db.Documents.Select(d => d.Tags).ToListAsync(ct);
            var usage = documentTagLists.Count(tags => tags.Contains(id));
            if (usage > 0)
                return Results.Conflict(new
                {
                    error = "tag_in_use",
                    message = $"タグ「{tag.Name}」は {usage} 件の文書で使われているため削除できません。",
                    usageCount = usage,
                });

            db.Tags.Remove(tag);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithName("DeleteTag");
    }
}
