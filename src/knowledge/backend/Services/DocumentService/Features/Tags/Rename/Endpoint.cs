using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Features.Documents;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.Tags.Rename;

// FR-09, SC-09, #635: 改名する（「改名は許して既存の文書が新しい名前へ追随する」）。
//
// **文書は 1 件も書き換えない。** 正本が識別子を持つので、**追随は表示の解決だけで起こる**
// （[[IADR-0153]] 決定 1）。**版も増えない**——改名は文書の内容変更ではない（同 決定 3）。
//
// **射影（Qdrant / Wiki.js）は書き換える必要がある**——あちらは表示名を焼き込んだ複写だからである。
// `DocumentUpdated` を再発行して作り直す（同 決定 3）。**再発行は既存の経路をそのまま使う**ので、
// 下流のサービスは変更しない。
internal static class RenameTagEndpoint
{
    internal static void Map(RouteGroupBuilder write)
    {
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
    }
}
