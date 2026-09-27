namespace DocumentService.Features.Documents.ListVersions;

// FR-06, UC-03: 版履歴一覧（新しい順）。
//
// NFR-09, ADR-0029, ADR-0075, [[IADR-0402]] (#1255): 本体は `DocumentReadUseCase` に在り、
// **east-west gRPC の面（`DocumentReadGrpcService`）と同じ関数を通る**（判定器を 2 つにしない）。
// 🔴 **「文書が無い」（404）と「版が無い」（200 の `[]`）の区別は use case が返す `null` / 空リスト**
// で表される。ここで畳んではならない。
// NFR-09, 計画 ADR-0119 決定 3, ADR-0056 (#1614): **認証を要する**。読めない個人資料は「文書が無い」と同じ 404。
internal static class ListDocumentVersionsEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/{id:guid}/versions", async (Guid id, HttpContext http, DocumentReadUseCase reads,
            CancellationToken ct) =>
        {
            var versions = await reads.ListVersionsAsync(DocumentReadPrincipal.FromUser(http.User), id, ct);
            return versions is null ? Results.NotFound() : Results.Ok(versions);
        });
    }
}
