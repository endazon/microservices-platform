using DocumentService.Domain;
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
// 認可: **認証を要する**（合成点の `read` 群。［2026-09-27 / #1614］読み取りの 5 口が同じ群になった）。
// 集合の性質（`GET /documents` の部分集合・個人資料を主体に依らず返さない）は `DocumentPageQuery` の注記を参照。
// 🔴 個人資料を主体に依らず外すので、`DocumentReadAccess` を通さなくても「他人の個人資料を返さない」は満たす
//   （所有者にも返さない＝より狭い）。
// ［2026-09-28 更新 / #1615］🔴 **内容の ABAC の門が開いたときだけ、この口も `DocumentReadAccess` を通す**（計画 ADR-0119 決定 3）。
//   絞るのは**切り出す前**である（切り出した後に落とすと、ページが短くなり続きのカーソルがずれる）。共有先は台帳の全件分を
//   1 クエリで引く（判定の像に要る）。門が閉じている間は従前どおり通さない（問い合わせも共有先の全件の読み出しも増やさない）。
internal static class ListDocumentPageEndpoint
{
    internal static void Map(RouteGroupBuilder pageRead)
    {
        pageRead.MapGet("/page", async (int? limit, string? cursor, HttpContext http,
            DocumentDbContext db, DocumentReadAccess access, CancellationToken ct) =>
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
            if (access.ContentAbacEnabled)
                ledger = await ReadableAsync(ledger, DocumentReadPrincipal.FromUser(http.User), db, access, ct);
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

    // #1615: 内容の ABAC の門が開いたときの絞り込み（切り出しの前）。
    private static async Task<List<Document>> ReadableAsync(List<Document> ledger, DocumentReadPrincipal principal,
        DocumentDbContext db, DocumentReadAccess access, CancellationToken ct)
    {
        var shares = await DocumentEndpoints.ResolveSharedWithAsync(db, ledger.Select(d => d.Id).ToList(), ct);
        var readable = new List<Document>(ledger.Count);
        foreach (var d in ledger)
        {
            if (await access.CanReadAsync(principal, d, shares.GetValueOrDefault(d.Id), ct))
                readable.Add(d);
        }
        return readable;
    }
}
