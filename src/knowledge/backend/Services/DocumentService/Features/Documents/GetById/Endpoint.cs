namespace DocumentService.Features.Documents.GetById;

// FR-06, UC-03: 文書 1 件の取得。
//
// NFR-09, ADR-0029, ADR-0075, [[IADR-0402]] (#1255): 本体は `DocumentReadUseCase` に在り、
// **east-west gRPC の面（`DocumentReadGrpcService`）と同じ関数を通る**（判定器を 2 つにしない）。
// 「無い」は use case が `null` で返し、**status を与えるのはここ**である（gRPC 側は `found=false`）。
// NFR-09, 計画 ADR-0119 決定 3, ADR-0056 (#1614): **認証を要する**。読めない個人資料は「無い」と同じ 404。
internal static class GetDocumentEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/{id:guid}", async (Guid id, HttpContext http, DocumentReadUseCase reads, CancellationToken ct) =>
        {
            var doc = await reads.GetAsync(DocumentReadPrincipal.FromUser(http.User), id, ct);
            return doc is null ? Results.NotFound() : Results.Ok(doc);
        });
    }
}
