using DocumentService.Api.Foundation.Domain;
using DocumentService.Api.Foundation.Persistence;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Api.Foundation.Endpoints;

// FR-06, FR-09, SC-05, SC-09, UC-03, UC-05, #634: タグ辞書（IADR-0152）。
//
// **辞書を DocumentService が持つのは、使用件数が文書の局所クエリになるためである**（IADR-0152 決定 1）。
// サービスを跨ぐと削除拒否の判定のたびに同期呼び出しが要り、
// 数え落としが「消してはいけないタグを消せる」事故になる。
//
// **改名・削除は #635 で足す**（保持方式を識別子参照へ移してからでないと、
// 改名と削除で前提が食い違う）。
public static class TagDictionaryEndpoints
{
    public static IEndpointRouteBuilder MapTagDictionaryEndpoints(this IEndpointRouteBuilder app)
    {
        // IADR-0044: 多層防御。BFF を迂回した直接呼び出しでも認可を実効化する（サービスが最終防衛線）。
        //
        // **読み取りは管理者・運用者**（SC-05 の裁定 Q18「読み取り専用の照会口を管理者・運用者へ開く」）。
        // **一般利用者はここを引かない** —— 一般利用者の候補は Qdrant の facet 経由である
        // （ADR-0043 決定 1 が辞書を丸ごと返すことを禁じている。IADR-0152 決定 4）。
        var read = app.MapGroup("/tags").WithTags("Tags")
            .RequireAuthorization(p => p.RequireRole(
                PlatformAuthPolicies.AdminRole,
                PlatformAuthPolicies.OperatorRole));

        // **追加はシステム管理者限定**（SC-09 のアクセス制御「システム管理者ロール限定」）。
        var write = app.MapGroup("/tags").WithTags("Tags")
            .RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        // FR-09, SC-05, SC-09: 値集合 ＋ タグごとの使用件数。
        read.MapGet("/", async (DocumentDbContext db, CancellationToken ct) =>
            Results.Ok(new TagDictionaryResponse(await LoadWithUsageAsync(db, ct))))
            .WithName("ListTags").Produces<TagDictionaryResponse>();

        // FR-09, SC-09: 辞書へタグを追加する。
        // **名前の重複は 409** —— SC-09 は「新しい名前は既存値と重複しない」と定めている。
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

        return app;
    }

    // FR-09, SC-09, #634: 辞書の全タグへ使用件数を添えて返す（IADR-0152 決定 2）。
    //
    // **数えるのは現行版の `Document.Tags` だけである。**
    // - **版履歴（`DocumentVersion`）は数えない** —— append-only で付け替えられないため、数えると
    //   一度でも使われたタグを永久に削除できず、SC-09 の「0 件のときに限り削除できる」が空文になる。
    // - **アーカイブ済みの文書は数える** —— アーカイブ済みでもタグは付け替えられる
    //   （`Document.UpdateMetadata` に状態の guard が無い。実測）ので、数えても管理者は行動できる。
    //
    // **［暫定］表示名の一致で数えている。** 文書はまだタグの識別子ではなく表示名を複写しており
    // （IADR-0152 決定 6。移行は **#635**）、識別子で数えられない。
    // **#635 が保持方式を移したら、ここは識別子の一致へ置き換える。**
    private static async Task<List<TagDto>> LoadWithUsageAsync(DocumentDbContext db, CancellationToken ct)
    {
        var tags = await db.Tags.OrderBy(t => t.Name).ToListAsync(ct);
        if (tags.Count == 0) return [];

        // `Tags` は jsonb へ変換した `List<string>` なので、SQL 側で展開して数えられない。
        // 辞書の規模（管理画面で人が管理する値集合）に対して、現行版の文書のタグだけを読む。
        var documentTagLists = await db.Documents.Select(d => d.Tags).ToListAsync(ct);

        var usage = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var documentTags in documentTagLists)
        {
            // **同じ文書に同じタグが 2 度入っていても 1 件と数える**（数えるのは「使っている文書の数」である）。
            foreach (var name in documentTags.Distinct(StringComparer.Ordinal))
                usage[name] = usage.GetValueOrDefault(name) + 1;
        }

        return [.. tags.Select(t => new TagDto(t.Id, t.Name, usage.GetValueOrDefault(t.Name)))];
    }
}
