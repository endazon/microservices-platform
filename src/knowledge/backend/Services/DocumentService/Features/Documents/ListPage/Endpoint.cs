using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.Documents.ListPage;

// FR-06, NFR-08, UC-03 (#1575): **組織文書の絞り込み・ページングの口**（`GET /documents/page`）。
//
//   `attr.<キー>=<値>`（0 個以上・AND・完全一致）／`limit`（既定 100・1〜500 に丸める）／
//   `cursor`（前ページの `nextCursor`）→ 200 `DocumentPageDto`。
//
// 🔴 **既存の `GET /documents` は変えない。** BFF の一覧（`FetchListAsync`）と gRPC `ListDocuments` が
// 「全件・同じ並び」を前提にしている。絞り込みはこの別の口にだけ置く（gRPC 面には足さない ——
// 呼び出し元が居ない。IADR-0401 決定 2 と同じ作法）。
//
// 認可: **認証を要する**（合成点の `pageRead` 群）。既存の `GET /documents` は認証すら要らないが、
// 新しい口は狭い側で開ける。集合の性質（`GET /documents` の部分集合・個人資料を返さない）は
// `DocumentPageQuery` の注記を参照。
internal static class ListDocumentPageEndpoint
{
    internal static void Map(RouteGroupBuilder pageRead)
    {
        pageRead.MapGet("/page", async (int? limit, string? cursor, HttpContext http,
            DocumentDbContext db, CancellationToken ct) =>
        {
            var (filters, problem) = DocumentPageQuery.ParseFilters(http.Request.Query);
            if (problem is not null) return problem;

            DocumentPageCursor? after = null;
            if (cursor is not null)
            {
                if (!DocumentPageCursor.TryDecode(cursor, out var decoded))
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["cursor"] = ["カーソルが不正です。前ページの nextCursor をそのまま渡してください。"],
                    });
                after = decoded;
            }

            // 台帳を読んでからメモリで絞る（属性は jsonb の値変換で SQL へ訳せない。
            // 既存の `GET /documents` と同じ読み方であり、DB の負荷は増えない）。
            var ledger = await db.Documents.AsNoTracking().ToListAsync(ct);
            var (page, next) = DocumentPageQuery.Slice(
                ledger, filters!, DocumentPageQuery.ClampLimit(limit), after);

            // 共有先は 1 クエリで引いて分配する（`DocumentReadUseCase.ListAsync` と同じ規律）。
            var names = await TagResolver.NamesAsync(db);
            var shares = await DocumentEndpoints.ResolveSharedWithAsync(
                db, page.Select(d => d.Id).ToList(), ct);
            return Results.Ok(new DocumentPageDto
            {
                Items = page
                    .Select(d => DocumentEndpoints.ToDto(d, names, shares.GetValueOrDefault(d.Id)))
                    .ToList(),
                NextCursor = next,
            });
        }).WithName("DocumentPage").Produces<DocumentPageDto>();
    }
}
