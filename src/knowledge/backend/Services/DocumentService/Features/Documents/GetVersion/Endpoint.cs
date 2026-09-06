namespace DocumentService.Features.Documents.GetVersion;

// FR-06, UC-03: 特定版の取得。
//
// NFR-09, ADR-0029, ADR-0075, [[IADR-0402]] (#1255): 本体は `DocumentReadUseCase` に在り、
// **east-west gRPC の面（`DocumentReadGrpcService`）と同じ関数を通る**（判定器を 2 つにしない）。
// 文書の不在と版の不在は**区別しない**（どちらも 404。gRPC 側はどちらも `found=false`）。
internal static class GetDocumentVersionEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/{id:guid}/versions/{version:int}", async (Guid id, int version,
            DocumentReadUseCase reads, CancellationToken ct) =>
        {
            var snapshot = await reads.GetVersionAsync(id, version, ct);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        });
    }
}
