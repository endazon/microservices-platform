using DocumentService.Features.Documents;
using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using DocumentService.Domain.Ports;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.Tags;

// FR-06, FR-09, SC-05, SC-09, UC-03, UC-05, #634: タグ辞書（IADR-0152）。
//
// **辞書を DocumentService が持つのは、使用件数が文書の局所クエリになるためである**（IADR-0152 決定 1）。
// サービスを跨ぐと削除拒否の判定のたびに同期呼び出しが要り、
// 数え落としが「消してはいけないタグを消せる」事故になる。
//
// **［#635］改名・削除を足した。** #634 で先送りしたのは、保持方式を識別子参照へ移す前だと
// 改名と削除で前提が食い違うためである（表示名を複写したままの改名は既存文書へ追随しない）。
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

        // FR-09, SC-09, #635: 改名する（「改名は許して既存の文書が新しい名前へ追随する」）。
        //
        // **文書は 1 件も書き換えない。** 正本が識別子を持つので、**追随は表示の解決だけで起こる**
        // （[[IADR-0153]] 決定 1）。**版も増えない**——改名は文書の内容変更ではない（同 決定 3）。
        //
        // **射影（Qdrant / Wiki.js）は書き換える必要がある**——あちらは表示名を焼き込んだ複写だからである。
        // `DocumentUpdated` を再発行して作り直す（同 決定 3）。**再発行は既存の経路をそのまま使う**ので、
        // 下流のサービスは変更しない。
        write.MapPut("/{id:guid}", async (Guid id, RenameTagRequest req, DocumentDbContext db,
            IDocumentUpdatedPublisher bus, CancellationToken ct) =>
        {
            var name = Tag.Normalize(req.Name ?? string.Empty);
            if (string.IsNullOrEmpty(name))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["name"] = ["タグ名は必須です。"],
                });

            var tag = await db.Tags.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (tag is null) return Results.NotFound();

            // SC-09「新しい名前は既存値と重複しない」。**自分自身は除く**——
            // 同じ名前への改名（実質の no-op）を 409 にしても利用者は何も直せない。
            if (await db.Tags.AnyAsync(t => t.Name == name && t.Id != id, ct))
                return Results.Conflict(new { message = $"タグ「{name}」は既に辞書にあります。" });

            tag.Rename(name);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // 追加と同じ race（検査と保存の間に別の要求が同名を入れる）。契約どおり 409 にする。
                return Results.Conflict(new { message = $"タグ「{name}」は既に辞書にあります。" });
            }

            // **改名したタグを使っている文書だけ再発行する。** 全文書を流すと辞書の 1 語の変更で
            // 索引全体が再構築され、規模に比例して費用が出る。
            //
            // **2 段に分けているのは、文書の実体を全件読まないためである。**
            // 1 段目は `Id` ＋ `Tags` だけを読んで対象を絞り（`LoadWithUsageAsync` と同じ形）、
            // 2 段目で**該当した文書だけ**実体を読む。改名は「使っているのは数件」が普通なので、
            // ここで全行（本文 URI・属性を含む）を読むと、費やす I/O が発行するイベント数に見合わない。
            //
            // **`Tags` の走査自体は全件のままである** —— jsonb へ変換した `List<Guid>` は SQL 側で
            // 展開できないためで、消したければ jsonb 包含（`@>`）＋ GIN 索引が要る。
            // ただし `FromSql` は EF InMemory で動かず、端点テストが全滅するため本 issue では採らない。
            var names = await TagResolver.NamesAsync(db, ct);
            var affectedIds = (await db.Documents
                    .Select(d => new { d.Id, d.Tags })
                    .ToListAsync(ct))
                .Where(d => d.Tags.Contains(id))
                .Select(d => d.Id)
                .ToList();

            var affected = affectedIds.Count == 0
                ? []
                : await db.Documents.Where(d => affectedIds.Contains(d.Id)).ToListAsync(ct);
            foreach (var doc in affected)
                await DocumentEndpoints.PublishUpdatedAsync(bus, doc, names, ct);

            // **再発行件数 ＝ 使用件数である**（どちらも「現行版でこのタグを持つ文書の数」。
            // `LoadWithUsageAsync` と同じ母集合を使っており、2 通りの数え方を持たない）。
            return Results.Ok(new RenameTagResponse(
                new TagDto(tag.Id, tag.Name, affected.Count), affected.Count));
        }).WithName("RenameTag").Produces<RenameTagResponse>();

        // FR-09, SC-09, #635: 削除する。**使用件数が 0 件のときだけ許す**
        //（SC-09「参照が 1 件でもあるタグは削除拒否」。[[IADR-0153]] 決定 6）。
        //
        // **件数を添えて 409 を返す**——SC-09 が「削除前に使用件数を示す」と定めており、
        // 数だけでも「まず 3 件を外してから消す」と管理者が行動できる。
        write.MapDelete("/{id:guid}", async (Guid id, DocumentDbContext db, CancellationToken ct) =>
        {
            var tag = await db.Tags.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (tag is null) return Results.NotFound();

            // **数え方は一覧と同じでなければならない**（`LoadWithUsageAsync` と同じ母集合）。
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
    // **［#635］識別子の一致で数える**（暫定だった表示名一致を置き換えた）。
    // 正本が識別子を持つようになったので、**改名しても件数は変わらない**——同じタグを指し続けるためである。
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
