namespace DocumentService.Features.Documents.List;

// FR-06, UC-03: 文書の一覧（更新の新しい順）。
// **ロールで塞がない**（SC-03 の一般利用者の閲覧。機密制御は取得段の ABAC が担う。IADR-0012）。
//
// NFR-09, ADR-0029, ADR-0075, [[IADR-0402]] (#1255): 本体は `DocumentReadUseCase` に在り、
// **east-west gRPC の面（`DocumentReadGrpcService`）と同じ関数を通る**（判定器を 2 つにしない）。
internal static class ListDocumentsEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/", async (DocumentReadUseCase reads, CancellationToken ct) =>
            Results.Ok(await reads.ListAsync(ct)));
    }
}
