using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.Tags.Create;

// FR-09, SC-09: 辞書へタグを追加する。
// **名前の重複は 409** —— SC-09 は「新しい名前は既存値と重複しない」と定めている。
internal static class CreateTagEndpoint
{
    internal static void Map(RouteGroupBuilder write)
    {
        write.MapPost("/", async (CreateTagRequest req, DocumentDbContext db, CancellationToken ct) =>
        {
            var name = Tag.Normalize(req.Name ?? string.Empty);
            if (string.IsNullOrEmpty(name))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["name"] = ["タグ名は必須です。"],
                });

            // **正規化後の名前で重複を見る**（前後の空白だけが違う 2 つを別物にしない）。
            if (await db.Tags.AnyAsync(t => t.Name == name, ct))
                return Results.Conflict(new { message = $"タグ「{name}」は既に辞書にあります。" });

            var tag = Tag.Create(name);
            db.Tags.Add(tag);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Name 一意制約違反。**上の事前検証をすり抜けた同時登録（race）**を 409 で返す。
                // 事前検証だけでは埋まらない —— 検査と保存の間に別の要求が同名を入れられる。
                // **契約は「重複は 409」なので、素の 500 にしない**
                // （`AuthzEndpoints` の `AttributeDefinition` が同型の先例である）。
                return Results.Conflict(new { message = $"タグ「{name}」は既に辞書にあります。" });
            }

            // 追加直後の使用件数は 0 である（まだどの文書にも付いていない）。
            return Results.Created($"/tags/{tag.Id}", new TagDto(tag.Id, tag.Name, 0));
        }).WithName("CreateTag").Produces<TagDto>(StatusCodes.Status201Created);
    }
}
