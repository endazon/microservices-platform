using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.Tags.List;

// FR-09, SC-05, SC-09: 値集合 ＋ タグごとの使用件数。
internal static class ListTagsEndpoint
{
    internal static void Map(RouteGroupBuilder read)
    {
        read.MapGet("/", async (DocumentDbContext db, CancellationToken ct) =>
            Results.Ok(new TagDictionaryResponse(await LoadWithUsageAsync(db, ct))))
            .WithName("ListTags").Produces<TagDictionaryResponse>();
    }

    // FR-09, SC-09, #634: 辞書の全タグへ使用件数を添えて返す（IADR-0152 決定 2）。
    //
    // **数えるのは現行版の `Document.Tags` だけである。**
    // - **版履歴（`DocumentVersion`）は数えない** —— append-only で付け替えられないため、数えると
    //   一度でも使われたタグを永久に削除できず、SC-09 の「0 件のときに限り削除できる」が空文になる。
    // - **アーカイブ済みの文書は数える** —— アーカイブ済みでもタグは付け替えられる
    //   （`Document.UpdateMetadata` に状態の guard が無い。実測）ので、数えても管理者は行動できる。
    //
    // **［#635］識別子の一致で数える**（暫定だった表示名一致を置き換えた）。
    // 正本が識別子を持つようになったので、**改名しても件数は変わらない**——同じタグを指し続けるためである。
    //
    // 🔴 **削除（`DeleteTagEndpoint`）は同じ母集合を自前で数えている。** 一覧が 0 件と表示したのに
    // 削除が 409 を返す（あるいはその逆）と管理者は辞書を信用できなくなるため、
    // **数え方を動かすときは必ず両方を見る**（IADR-0153 決定 6）。
    private static async Task<List<TagDto>> LoadWithUsageAsync(DocumentDbContext db, CancellationToken ct)
    {
        var tags = await db.Tags.OrderBy(t => t.Name).ToListAsync(ct);
        if (tags.Count == 0) return [];

        // `Tags` は jsonb へ変換した `List<Guid>` なので、SQL 側で展開して数えられない。
        // 辞書の規模（管理画面で人が管理する値集合）に対して、現行版の文書のタグだけを読む。
        var documentTagLists = await db.Documents.Select(d => d.Tags).ToListAsync(ct);

        var usage = new Dictionary<Guid, int>();
        foreach (var documentTags in documentTagLists)
        {
            // **同じ文書に同じタグが 2 度入っていても 1 件と数える**（数えるのは「使っている文書の数」である）。
            foreach (var id in documentTags.Distinct())
                usage[id] = usage.GetValueOrDefault(id) + 1;
        }

        return [.. tags.Select(t => new TagDto(t.Id, t.Name, usage.GetValueOrDefault(t.Id)))];
    }
}
